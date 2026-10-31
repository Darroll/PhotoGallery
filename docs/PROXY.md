# Sharing outside your home: proxy and tunnel setups

PhotoGallery listens on plain HTTP on port 8080. To let people outside your home reach it, put something in front that provides HTTPS. **Never forward port 8080 from your router.** Pick one of these:

| Option | Good for | Needs |
|---|---|---|
| [SWAG](#swag) | You already run it, or want everything on your own server | A domain, router port 443 forwarded to SWAG |
| [Nginx Proxy Manager](#nginx-proxy-manager) | A point-and-click web screen | A domain, router port 443 forwarded to NPM |
| [Cloudflare Tunnel](#cloudflare-tunnel) | No router changes at all | A domain on Cloudflare (free plan works) |

Whatever you choose, the front end must:

1. **Pass the visitor's address and the original host.** `X-Forwarded-For`, `X-Forwarded-Proto` and `Host` (Cloudflare sends `CF-Connecting-IP`). Without them every visitor looks like the proxy, so statistics and rate limits are wrong, and sign-in cookies may not be marked secure.
2. **Allow big uploads.** At least `MAX_UPLOAD_MB` (default 90 MB). Cloudflare's free plan stops at 100 MB per request, which is why the default is below it.
3. **Not buffer responses**, so a large ZIP download starts at once instead of waiting for the whole file.
4. **Be listed in `TRUSTED_PROXIES`** (see below). The app ignores those headers from any other address.

After it works, open **Admin -> Overview**. The *Reverse proxy* and *Port exposure* checks turn amber if the proxy is not trusted or the port is reachable straight from the internet, and name the address to fix.

## Which address goes in `TRUSTED_PROXIES`?

The address the *proxy connects from, as the app sees it*. The default (`172.16.0.0/12,127.0.0.0/8,::1/128`) already covers Docker's own networks, which is right when the proxy is a container on the same Unraid server. If the Overview says requests from some address carried proxy headers that were ignored, add that address, comma separated:

```
TRUSTED_PROXIES=172.16.0.0/12,127.0.0.0/8,::1/128,192.168.1.50/32
```

Use `/32` for a single address. Never add a whole home network such as `192.168.1.0/24`: then any device on it could fake a visitor's address.

## SWAG

A ready config is in the repository: [`unraid/gallery.subdomain.conf`](../unraid/gallery.subdomain.conf). Save it as `/mnt/user/appdata/swag/nginx/proxy-confs/gallery.subdomain.conf`, add `gallery` to SWAG's subdomains, point DNS at your home address, and put the PhotoGallery container on the same Docker network as SWAG. It already sets the upload size, long timeouts and streaming.

## Nginx Proxy Manager

1. In Nginx Proxy Manager choose **Hosts -> Proxy Hosts -> Add Proxy Host**.
2. **Details** tab:
   - Domain Names: `gallery.example.com` (your own name).
   - Scheme: `http`.
   - Forward Hostname / IP: the container name `photogallery` if Nginx Proxy Manager and PhotoGallery share a custom Docker network, otherwise your server's address (for example `192.168.1.10`).
   - Forward Port: `8080`.
   - Turn on **Block Common Exploits**. Websockets are not needed.
3. **SSL** tab: request a new certificate (Let's Encrypt), turn on **Force SSL** and **HTTP/2 Support**.
4. **Advanced** tab, paste into *Custom Nginx Configuration*:

   ```nginx
   client_max_body_size 95m;     # at least MAX_UPLOAD_MB; Cloudflare caps at 100m
   proxy_read_timeout 900s;      # large ZIP and lossless downloads
   proxy_send_timeout 900s;
   proxy_request_buffering off;
   proxy_buffering off;          # stream ZIPs instead of buffering them
   proxy_buffer_size 16k;        # single sign-on cookies make response headers large
   ```

5. Save. Nginx Proxy Manager already sends `Host`, `X-Forwarded-For` and `X-Forwarded-Proto`.
6. Check `TRUSTED_PROXIES`. If PhotoGallery uses the default bridge network and Nginx Proxy Manager reaches it through the published port, the app sees Docker's gateway address, which the default already trusts. If Nginx Proxy Manager has its own LAN address (for example on `br0`), add that address as `/32`.
7. Protect `/admin`: add an **Access List** (user name and password, or allowed addresses) to the proxy host, or use Cloudflare Access in front.

## Cloudflare Tunnel

A tunnel makes an outbound connection from your server to Cloudflare, so you open no ports at all.

1. In the Cloudflare dashboard open **Zero Trust -> Networks -> Tunnels -> Create a tunnel** (type *Cloudflared*), and name it.
2. Run the connector it shows you. On Unraid the simplest way is a `cloudflared` container with your tunnel token; put it on the same Docker network as PhotoGallery.
3. Add a **Public hostname**:
   - Subdomain and domain: `gallery.example.com`.
   - Service type `HTTP`, URL `photogallery:8080` (container name when on the same network) or `192.168.1.10:8080`.
4. Visitors arrive as `CF-Connecting-IP`. The app believes that header only when the connection comes from a trusted address, so the `cloudflared` container's address must be covered by `TRUSTED_PROXIES`. On a shared Docker network it already is; if `cloudflared` runs on another machine, add its LAN address as `/32`.
5. Protect `/admin` with **Cloudflare Access**: Zero Trust -> Access -> Applications -> add a self-hosted application for `gallery.example.com/admin` (and `/api/admin`), with a policy that allows only your email address.
6. The free plan caps a request at 100 MB, so keep `MAX_UPLOAD_MB` at 90. Check Cloudflare's terms before serving very large amounts of media through it.
7. Keep the proxy on (orange cloud). Country in the visitor statistics is only known while Cloudflare proxies the site.

Because the tunnel connects from inside your network, the *Port exposure* check stays green, which is what you want: nothing reaches the app directly from the internet.

## Check it

- Open the public address from your phone on mobile data (not your home Wi-Fi) and sign in to `/admin`.
- Open **Admin -> Overview**: no amber *Reverse proxy* or *Port exposure*.
- **Admin -> Statistics** shows your phone's address, not the proxy's. If it shows the proxy, fix `TRUSTED_PROXIES`.
- Upload a photo as a guest, and download a gallery ZIP. Failures here usually mean the upload size or buffering lines above are missing.

More help: [Troubleshooting](TROUBLESHOOTING.md).
