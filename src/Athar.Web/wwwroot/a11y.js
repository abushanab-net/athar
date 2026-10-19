// MudBlazor 8 puts a select's label and aria-label on a hidden <input>; the element that takes focus is a bare <div>.
// Give that div the combobox role and the name so screen readers announce every MudSelect (T085).
// ponytail: DOM patch over a library gap; drop it when MudBlazor names the focusable element itself.
(() => {
    const name = (select) => {
        const input = select.querySelector('input[type=hidden]');
        return input?.getAttribute('aria-label') || select.querySelector('label')?.textContent?.trim();
    };
    const fix = () => document.querySelectorAll('.mud-select').forEach((select) => {
        const box = select.querySelector('.mud-select-input[tabindex="0"]');
        const label = name(select);
        if (!box || !label || box.getAttribute('aria-label') === label) return;
        box.setAttribute('role', 'combobox');
        box.setAttribute('aria-haspopup', 'listbox');
        box.setAttribute('aria-label', label);
    });
    // A chip's close button says only "Remove"; add what it removes.
    const chips = () => document.querySelectorAll('.mud-chip').forEach((chip) => {
        const close = chip.querySelector('.mud-chip-close-button');
        const text = chip.querySelector('.mud-chip-content')?.textContent?.trim();
        if (!close || !text) return;
        close.dataset.base ??= close.getAttribute('aria-label') ?? '';
        const label = `${close.dataset.base} ${text}`.trim();
        if (close.getAttribute('aria-label') !== label) close.setAttribute('aria-label', label);
    });
    new MutationObserver(() => { fix(); chips(); }).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['tabindex'] });
    fix();
    chips();
})();
