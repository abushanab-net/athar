"""T096: check the hosts behind the proxy from outside (AT-20 and the docs/03 §2 path rules). Standard library only.

  python tools/check_public.py --share https://athar-share.example.com/s/<token> [--password P] [--gone URL] [--rate-limit]
                               [--mcp https://athar-mcp.example.com] [--app https://athar.example.com]

--share     a live share link (make one in the wiki's Share tab; with --password, give it that password).
--gone      a revoked or expired share link: expects 410.
--rate-limit  sends 6 wrong passwords and expects 429. This locks the --share link for 15 minutes from this address.
--mcp       the agent host: /mcp needs a bearer token, OAuth metadata is public, wiki and share paths are 404.
--app       the portal (or the LAN address): /collab upgrades to a WebSocket, collab's own routes stay unreachable.
Exit code 1 if any check fails.
"""
import argparse, http.client, http.cookiejar, re, sys, urllib.error, urllib.parse, urllib.request, uuid

failures = 0

def check(ok, what, detail=""):
    global failures
    print(f"{'PASS' if ok else 'FAIL'}  {what}{'' if ok else f'  ({detail})'}")
    failures += not ok

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs): return None

jar = http.cookiejar.CookieJar()
opener = urllib.request.build_opener(NoRedirect, urllib.request.HTTPCookieProcessor(jar))

def get(url, data=None, headers=None, body=None):
    """(status, headers, body) without following redirects; HTTP errors are results, not exceptions. `data` posts a form."""
    req = urllib.request.Request(url, data=urllib.parse.urlencode(data).encode() if data else body, headers=headers or {})
    try:
        with opener.open(req, timeout=20) as r: return r.status, r.headers, r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, e.headers, e.read().decode("utf-8", "replace")

def form(html):
    m = re.search(r'name="([^"]+)" value="([^"]+)"', html)
    return {m.group(1): m.group(2).replace("&#x2B;", "+").replace("&amp;", "&")} if m else {}

def share_host(link, password, gone, rate_limit):
    origin = "{0.scheme}://{0.netloc}".format(urllib.parse.urlsplit(link))
    path = urllib.parse.urlsplit(link).path.rstrip("/")
    status, h, html = get(link)
    check(status == 200, "share link opens", status)
    check(h.get("Cache-Control") == "no-store", "share page is no-store", h.get("Cache-Control"))
    check(h.get("Referrer-Policy") == "no-referrer", "share page sends no referrer", h.get("Referrer-Policy"))
    check("noindex" in (h.get("X-Robots-Tag") or ""), "share page isn't indexed", h.get("X-Robots-Tag"))
    if password:
        check('name="password"' in html, "password form first")
        status, _, _ = get(link + "/unlock", {**form(html), "password": password + "-wrong"})
        check(status == 401, "wrong password refused", status)
        status, h, _ = get(link + "/unlock", {**form(get(link)[2]), "password": password})
        check(status in (302, 303) and (h.get("Location") or "").endswith(path), "right password unlocks", f"{status} {h.get('Location')}")
        status, h, html = get(link)
        check(status == 200 and "<main" in html and 'name="password"' not in html, "unlocked page is shown", status)
    # T120: a page is scriptless unless it has a ```mermaid diagram; then it loads exactly our one script, and only then
    # does its CSP allow same-origin scripts.
    scripts = re.findall(r"<script\b[^>]*>", html)
    diagrams = 'class="language-mermaid"' in html
    check("contenteditable" not in html and all('src="/editor/athar-mermaid.js"' in s for s in scripts) and (not scripts or diagrams),
          "page is read-only; no scripts unless it has a diagram, then only /editor/athar-mermaid.js", scripts)
    csp = h.get("Content-Security-Policy") or ""
    check(("script-src 'self'" in csp) == bool(scripts), "CSP allows scripts only on a page with a diagram", csp)
    if scripts:
        check(get(origin + "/editor/athar-mermaid.js")[0] == 200, "the diagram script loads on the share host")
    for src in sorted(set(re.findall(r'(?:src|href)="(/s/[^"]+/attachments/[0-9a-f-]{36})"', html))):
        status, h, _ = get(origin + src)
        check(status == 200 and h.get("X-Content-Type-Options") == "nosniff" and "sandbox" in (h.get("Content-Security-Policy") or ""),
              f"shared file {src.rsplit('/', 1)[-1]} served sandboxed", status)
    for p in ["/", "/wiki", "/welcome", "/account/login", "/api/v1/inbox", "/mcp", "/collab", f"/attachments/{uuid.uuid4()}",
              "/_framework/blazor.web.js", "/oauth/authorize", f"{path}/attachments/{uuid.uuid4()}"]:
        check(get(origin + p)[0] == 404, f"share host 404 for {p}", get(origin + p)[0])
    if gone:
        check(get(gone)[0] == 410, "revoked or expired link is 410", get(gone)[0])
    if rate_limit and password:
        jar.clear()  # signed out of the link again, so each try gets the password form and its antiforgery field
        statuses = [get(link + "/unlock", {**form(get(link)[2]), "password": "nope"})[0] for _ in range(6)]
        check(statuses[-1] == 429, "wrong passwords are rate-limited", statuses)

def agent_host(origin):
    # MCP is stateless (POST only): an initialize without a token must be refused, never answered.
    status = get(origin + "/mcp", headers={"Content-Type": "application/json", "Accept": "application/json, text/event-stream"},
                 body=b'{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}')[0]
    check(status == 401, "agent host: /mcp needs a token", status)
    check(get(origin + "/.well-known/oauth-protected-resource")[0] == 200, "agent host: OAuth metadata is public")
    check(get(origin + "/agent/setup.sh")[0] == 200, "agent host: the setup script is served (T132)")
    for p in ["/", "/wiki", "/account/login", "/s/x", "/collab", f"/attachments/{uuid.uuid4()}"]:
        check(get(origin + p)[0] == 404, f"agent host 404 for {p}", get(origin + p)[0])

def app_host(origin):
    u = urllib.parse.urlsplit(origin)
    conn = (http.client.HTTPSConnection if u.scheme == "https" else http.client.HTTPConnection)(u.netloc, timeout=20)
    conn.request("GET", "/collab", headers={"Connection": "Upgrade", "Upgrade": "websocket", "Sec-WebSocket-Version": "13",
                                            "Sec-WebSocket-Key": "dGhlIHNhbXBsZSBub25jZQ=="})
    check(conn.getresponse().status == 101, "app host: /collab upgrades to the editor WebSocket")
    conn.close()
    check(get(origin + f"/collab/documents/{uuid.uuid4()}")[0] in (401, 404), "app host: collab's own routes aren't proxied")

if __name__ == "__main__":
    a = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    a.add_argument("--share"); a.add_argument("--password"); a.add_argument("--gone"); a.add_argument("--rate-limit", action="store_true")
    a.add_argument("--mcp"); a.add_argument("--app")
    args = a.parse_args()
    if not (args.share or args.mcp or args.app): a.error("give --share, --mcp or --app")
    if args.share: share_host(args.share.rstrip("/"), args.password, args.gone, args.rate_limit)
    if args.mcp: agent_host(args.mcp.rstrip("/"))
    if args.app: app_host(args.app.rstrip("/"))
    print(f"\n{'all checks passed' if not failures else f'{failures} check(s) failed'}")
    sys.exit(1 if failures else 0)
