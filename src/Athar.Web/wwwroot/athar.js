// Shell helpers: the theme preference (system / light / dark, kept in this browser) and the search shortcut.
window.athar = {
    focus: id => document.getElementById(id)?.focus(),
    hash: () => location.hash,
    // After a hidden section is shown, the browser's own jump to the #anchor has already missed it: jump again.
    scrollToHash: () => requestAnimationFrame(() => document.getElementById(decodeURIComponent(location.hash.slice(1)))?.scrollIntoView()),
    // The shell's breakpoint (MudBlazor Md, 960px): above it the menu button folds the sidebar to a rail.
    wide: () => matchMedia("(min-width: 960px)").matches,
    rail: {
        get: () => { try { return matchMedia("(min-width: 960px)").matches && localStorage.getItem("athar-rail") === "1"; } catch { return false; } },
        set: v => { try { localStorage.setItem("athar-rail", v ? "1" : "0"); } catch { } },
    },
    // Scroll spy (/help): marks the index link of the section nearest the top of the viewport. Re-arming replaces the observer.
    spy: (sections, links) => {
        window.athar._spy?.disconnect();
        const mark = id => document.querySelectorAll(links).forEach(a => a.dataset.spy === id ? a.setAttribute("aria-current", "location") : a.removeAttribute("aria-current"));
        const io = new IntersectionObserver(entries => {
            const top = entries.filter(e => e.isIntersecting).sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top)[0];
            if (top) mark(top.target.dataset.spy);
        }, { rootMargin: "-72px 0px -70% 0px" });
        document.querySelectorAll(sections).forEach(s => io.observe(s));
        window.athar._spy = io;
    },
    theme: {
        get: () => { try { return localStorage.getItem("athar-theme") || "system"; } catch { return "system"; } },
        set: v => { try { localStorage.setItem("athar-theme", v); } catch { } },
    },
    // Page tours (T152, PageTour.razor): Blazor renders the step; this places the spotlight and the card next to the target.
    // A browser that can't store the "seen" flag never auto-starts a tour (better quiet than repeating it every visit).
    tour: {
        current: null,
        seen: id => { try { return localStorage.getItem("athar-tour-" + id) === "1"; } catch { return true; } },
        done: id => { try { localStorage.setItem("athar-tour-" + id, "1"); } catch { } },
        visible: selector => { const el = document.querySelector(selector); return !!el && el.getClientRects().length > 0; },
        place: (selector, scroll = true) => {
            const t = window.athar.tour, el = document.querySelector(selector);
            const spot = document.getElementById("tour-spot"), card = document.getElementById("tour-card");
            t.current = selector;
            if (!spot || !card) return;
            if (!el || el.getClientRects().length === 0) { spot.hidden = true; card.classList.add("centered"); card.style.top = card.style.left = ""; card.focus({ preventScroll: true }); return; }
            if (scroll) el.scrollIntoView({ block: "nearest", inline: "nearest" });
            const r = el.getBoundingClientRect(), pad = 6, gap = 12, rtl = document.documentElement.dir === "rtl";
            spot.hidden = false;
            Object.assign(spot.style, { transform: `translate(${r.left - pad}px, ${r.top - pad}px)`, width: `${r.width + pad * 2}px`, height: `${r.height + pad * 2}px` });
            card.classList.remove("centered");
            const cw = card.offsetWidth, ch = card.offsetHeight, clampX = x => Math.min(Math.max(8, x), innerWidth - cw - 8), clampY = y => Math.min(Math.max(8, y), innerHeight - ch - 8);
            let top, left;
            if (r.height > innerHeight * 0.45) {
                // A tall target (the sidebar, the page tree): the card sits beside it, on the reading side.
                top = clampY(r.top + 16);
                left = clampX(rtl ? r.left - pad - gap - cw : r.right + pad + gap);
            } else {
                top = r.bottom + pad + gap + ch <= innerHeight - 8 ? r.bottom + pad + gap : clampY(r.top - pad - gap - ch);
                left = clampX(rtl ? r.right - cw : r.left);
            }
            Object.assign(card.style, { top: `${top}px`, left: `${left}px` });
            if (scroll) card.focus({ preventScroll: true });
        },
        end: () => { window.athar.tour.current = null; },
    },
};
// Keep the spotlight on its target while the page scrolls or the window resizes (without scrolling it back into view).
// Esc ends a tour wherever focus is: a re-rendered step card can drop focus to the body before it is placed.
document.addEventListener("keydown", e => { if (e.key === "Escape") document.querySelector("#tour-card .tour-skip")?.click(); });
["resize", "scroll"].forEach(type => addEventListener(type, () => { const s = window.athar.tour.current; if (s) window.athar.tour.place(s, false); }, { passive: true, capture: true }));

// "/" or Ctrl/Cmd+K focuses the top-bar search, unless the person is typing somewhere.
document.addEventListener("keydown", e => {
    const t = e.target;
    const typing = t instanceof HTMLElement && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName));
    if ((e.key === "/" && !typing && !e.ctrlKey && !e.metaKey) || (e.key.toLowerCase() === "k" && (e.ctrlKey || e.metaKey))) {
        const box = document.getElementById("athar-search");
        if (!box) return;
        e.preventDefault();
        box.focus();
        box.select();
    }
});

// Sort headers (SortTh) are role=button spans: Enter and Space sort, like a real button.
document.addEventListener("keydown", e => {
    const t = e.target;
    if ((e.key === "Enter" || e.key === " ") && t instanceof HTMLElement && t.matches(".mud-table-sort-label[role=button]")) {
        e.preventDefault();
        t.click();
    }
});
