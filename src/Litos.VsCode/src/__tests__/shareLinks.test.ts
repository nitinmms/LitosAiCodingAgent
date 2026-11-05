import { describe, it, expect } from "vitest";
import { rewriteStaleHostLink } from "../shareLinks";

const CURRENT = "http://127.0.0.1:60000";

describe("rewriteStaleHostLink", () => {
    it("re-points a share_file link from a dead port at the running host", () => {
        expect(rewriteStaleHostLink("http://127.0.0.1:51234/files/0123456789abcdef", CURRENT))
            .toBe(`${CURRENT}/files/0123456789abcdef`);
    });

    it("re-points an artifact link at the running host", () => {
        expect(rewriteStaleHostLink("http://127.0.0.1:51234/artifacts/0123456789abcdef", CURRENT))
            .toBe(`${CURRENT}/artifacts/0123456789abcdef`);
    });

    it("re-points a versioned artifact link, keeping the version", () => {
        expect(rewriteStaleHostLink("http://127.0.0.1:51234/artifacts/0123456789abcdef/v/3", CURRENT))
            .toBe(`${CURRENT}/artifacts/0123456789abcdef/v/3`);
    });

    it("accepts localhost as well as 127.0.0.1, and a trailing slash on the base URL", () => {
        expect(rewriteStaleHostLink("http://localhost:51234/artifacts/0123456789abcdef", `${CURRENT}/`))
            .toBe(`${CURRENT}/artifacts/0123456789abcdef`);
    });

    it.each([
        ["a non-loopback host with the same path shape", "https://example.com/artifacts/0123456789abcdef"],
        ["an artifact id of the wrong length", "http://127.0.0.1:51234/artifacts/0123abcd"],
        ["an upper-case artifact id (never minted)", "http://127.0.0.1:51234/artifacts/0123456789ABCDEF"],
        ["some other path on the host", "http://127.0.0.1:51234/config/status"],
        ["anything after an artifact id", "http://127.0.0.1:51234/artifacts/0123456789abcdef/meta.json"],
        ["an unparseable URL", "not a url"],
    ])("leaves %s unchanged", (_label, url) => {
        expect(rewriteStaleHostLink(url, CURRENT)).toBe(url);
    });

    it("leaves every link unchanged when no host is running", () => {
        const url = "http://127.0.0.1:51234/artifacts/0123456789abcdef";
        expect(rewriteStaleHostLink(url, undefined)).toBe(url);
    });
});
