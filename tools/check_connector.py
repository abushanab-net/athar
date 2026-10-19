"""T099 (AT-31a): can Claude's hosted apps (claude.ai, Desktop, mobile) connect to this MCP host? Run it from outside the network
before the manual run. It checks what Claude's OAuth client needs and stops before anyone signs in:

    python tools/check_connector.py https://athar-mcp.example.com/mcp
    python tools/check_connector.py https://localhost:7100/mcp --insecure      # the dev AppHost

Exit code 1 if any check fails.
"""
import argparse, json, ssl, sys, urllib.error, urllib.parse, urllib.request

CLIENT = "claude-ai"  # OAuthClients.ClaudeAi
CALLBACK = "https://claude.ai/api/mcp/auth_callback"
failed = False


def check(ok, what, detail=""):
    global failed
    failed |= not ok
    print(("PASS " if ok else "FAIL ") + what + (f"  ({detail})" if detail and not ok else ""))


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs): return None


def request(url, ctx, data=None, headers=None, cookie=None):
    r = urllib.request.Request(url, data=data, headers={**(headers or {}), **({"Cookie": cookie} if cookie else {})})
    try:
        resp = urllib.request.build_opener(NoRedirect, urllib.request.HTTPSHandler(context=ctx)).open(r, timeout=10)
    except urllib.error.HTTPError as e:
        resp = e
    return resp.status, {k.lower(): v for k, v in resp.headers.items()}, resp.read().decode("utf-8", "replace")  # names are case-insensitive


def main():
    a = argparse.ArgumentParser()
    a.add_argument("mcp_url", help="the MCP server URL exactly as people enter it in Claude")
    a.add_argument("--insecure", action="store_true", help="accept a dev certificate")
    args = a.parse_args()
    ctx = ssl._create_unverified_context() if args.insecure else ssl.create_default_context()
    mcp = args.mcp_url
    origin = "{0.scheme}://{0.netloc}".format(urllib.parse.urlparse(mcp))

    # 1. Claude starts sign-in only on a 401, and reads the metadata pointer from it.
    status, headers, _ = request(mcp, ctx, data=b"{}", headers={"Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    challenge = headers.get("www-authenticate", "")
    check(status == 401, "POST /mcp without a token is 401", status)
    pointer = challenge.split('resource_metadata="')[1].split('"')[0] if 'resource_metadata="' in challenge else ""
    check(pointer.startswith(origin + "/"), "the 401 points at metadata on this https origin", challenge or "no WWW-Authenticate")

    # 2. Protected resource metadata (RFC 9728): resource is the URL as entered; the first authorization server is used.
    status, _, body = request(pointer or origin + "/.well-known/oauth-protected-resource", ctx)
    prm = json.loads(body) if status == 200 else {}
    check(status == 200, "protected resource metadata is public", status)
    check(prm.get("resource") == mcp, "resource equals the MCP URL exactly", prm.get("resource"))
    issuer = (prm.get("authorization_servers") or [""])[0]
    check(issuer.startswith("https://"), "first authorization server is https", issuer)
    if not issuer.startswith("http"):
        sys.exit("Stopped: no authorization server to check. Is the host routed to Athar (Pangolin resource, DNS)?")

    # 3. Authorization server metadata (RFC 8414) at the issuer, with S256 PKCE and https endpoints.
    status, _, body = request(issuer.rstrip("/") + "/.well-known/oauth-authorization-server", ctx)
    asm = json.loads(body) if status == 200 else {}
    check(status == 200, "authorization server metadata is public", status)
    check(asm.get("issuer") == issuer, "issuer matches", asm.get("issuer"))
    check("S256" in asm.get("code_challenge_methods_supported", []), "S256 PKCE is advertised")
    for key in ("authorization_endpoint", "token_endpoint"):
        check(str(asm.get(key, "")).startswith("https://"), f"{key} is https", asm.get(key))
    check("offline_access" in asm.get("scopes_supported", []), "offline_access is offered (refresh tokens)")

    # 4. The client is registered here with Claude's callback: authorize goes to sign-in, not an error page.
    q = urllib.parse.urlencode({"client_id": CLIENT, "response_type": "code", "redirect_uri": CALLBACK, "scope": "read write offline_access",
                                "state": "check", "resource": mcp, "code_challenge": "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
                                "code_challenge_method": "S256"})
    status, headers, _ = request(asm.get("authorization_endpoint", origin + "/oauth/authorize") + "?" + q, ctx)
    check(status == 302 and "/oauth/login" in headers.get("location", ""), f"'{CLIENT}' with Claude's callback is sent to sign-in", f"{status} {headers.get('location', '')}")

    # 5. AT-31a: a cookie means nothing to the token endpoint and to /mcp.
    status, _, body = request(asm.get("token_endpoint", origin + "/oauth/token"), ctx, data=b"grant_type=authorization_code&code=x",
                              headers={"Content-Type": "application/x-www-form-urlencoded"}, cookie="athar.oauth=forged; .AspNetCore.Identity.Application=forged")
    check(status == 400 and "invalid" in body, "/oauth/token ignores cookies (400 invalid_request/client)", status)
    status, _, _ = request(mcp, ctx, data=b"{}", headers={"Content-Type": "application/json"}, cookie=".AspNetCore.Identity.Application=forged")
    check(status == 401, "/mcp ignores cookies", status)

    print("All checks passed: add the connector in Claude (see deploy/README.md)." if not failed else "Some checks failed.")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
