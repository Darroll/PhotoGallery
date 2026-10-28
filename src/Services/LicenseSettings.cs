using System.Security.Cryptography;
using System.Text;

namespace PhotoGallery;

/// <summary>Photo license shown to visitors and (optionally) accepted before a download. Editable in Admin -> Settings.</summary>
/// <summary>The public form of the license: placeholders filled in, plus a version id.</summary>
public record LicenseView(bool Enabled, bool Require, string Title, string Text, string Version);

public class LicenseSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Visitors must tick "I agree" in the download dialog before they can download.</summary>
    public bool RequireAcceptance { get; set; } = true;
    public string Title { get; set; } = "Photo License Agreement";
    /// <summary>Name of the copyright owner; falls back to the site name when empty.</summary>
    public string Holder { get; set; } = "";
    /// <summary>Contact for permission requests (email or web address); optional.</summary>
    public string Contact { get; set; } = "";
    /// <summary>License text. {holder}, {site}, {year} and {contact} are replaced when it is shown.</summary>
    public string Text { get; set; } = DefaultText;

    public const int MaxText = 20000;

    public const string DefaultText = """
        1. Ownership
        All photographs on this site are copyrighted works owned by {holder}. They are licensed to you, not sold. Downloading a photograph does not transfer any ownership or copyright to you.

        2. License granted
        Subject to these terms, {holder} grants you a non-exclusive, non-transferable, non-sublicensable, revocable license to download and use the photographs for personal, non-commercial purposes only. Examples of permitted use: printing for your own home, sharing with family and friends, and posting on your personal social media accounts with credit to {holder}.

        3. Restrictions
        You may not, and may not allow anyone else to:
        a) sell, resell, license, sublicense, rent or otherwise make money from the photographs, or products made from them (prints, merchandise, stock images, photo books, templates and similar);
        b) use the photographs in advertising, promotion, marketing, paid publications or any other commercial context, including by a business or organization;
        c) upload the photographs to stock, print-on-demand, NFT or marketplace sites;
        d) remove or alter watermarks, metadata, copyright notices or credits;
        e) use the photographs to train, fine-tune or test machine-learning or AI models, or submit them to AI image generation services;
        f) represent the photographs as your own work.

        4. Commercial and other uses
        Any use not expressly allowed above requires prior written permission from {holder}. To request a commercial license, contact {contact}.

        5. People shown in the photographs
        Where a photograph shows an identifiable person, this license does not give you the right to use that person's name or likeness commercially. Any such use needs that person's consent as well.

        6. Termination
        This license ends automatically if you breach any of its terms. You must then stop using the photographs and delete all copies. {holder} may also withdraw access to the photographs at any time.

        7. No warranty and limitation of liability
        The photographs are provided "as is" without warranties of any kind. To the fullest extent permitted by law, {holder} is not liable for any loss or damage arising from your use of the photographs.

        8. Enforcement
        Unauthorized use infringes copyright. {holder} reserves the right to take action to stop it and to claim damages, license fees and legal costs, to the extent the law allows.

        9. Acceptance
        By downloading a photograph from this site you confirm that you have read and agree to this agreement. If you do not agree, do not download.

        Copyright (c) {year} {holder}. All rights reserved.
        """;

    public LicenseSettings Sanitized()
    {
        static string Clip(string? s, int max) { s = (s ?? "").Trim(); return s.Length > max ? s[..max] : s; }
        var text = (Text ?? "").Replace("\r\n", "\n").Trim();
        if (text.Length > MaxText) text = text[..MaxText];
        return new LicenseSettings
        {
            Enabled = Enabled, RequireAcceptance = RequireAcceptance,
            Title = Clip(Title, 100) is { Length: > 0 } t ? t : "Photo License Agreement",
            Holder = Clip(Holder, 100), Contact = Clip(Contact, 200),
            Text = text.Length == 0 ? DefaultText : text
        };
    }

    public LicenseSettings Clone() => new()
    {
        Enabled = Enabled, RequireAcceptance = RequireAcceptance, Title = Title, Holder = Holder, Contact = Contact, Text = Text
    };

    /// <summary>What visitors see: placeholders filled in, plus a version id that changes whenever the wording changes (so people re-accept).</summary>
    public LicenseView ForVisitors(string siteName)
    {
        var holder = Holder.Length > 0 ? Holder : siteName;
        var contact = Contact.Length > 0 ? Contact : $"{holder} through this site";
        var text = Text.Replace("{holder}", holder).Replace("{site}", siteName)
            .Replace("{year}", DateTime.UtcNow.Year.ToString()).Replace("{contact}", contact);
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Title + "\n" + text)))[..12].ToLowerInvariant();
        return new LicenseView(Enabled, Enabled && RequireAcceptance, Title, text, version);
    }
}
