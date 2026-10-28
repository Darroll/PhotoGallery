namespace PhotoGallery;

/// <summary>Someone tagged in photos. Their name is shown to visitors, and searchable by them, only when Public is set.</summary>
public class Person
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Public { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Links a person to a photo.</summary>
public class PhotoPerson
{
    public int PhotoId { get; set; }
    public int PersonId { get; set; }
}
