const animations = new WeakMap<HTMLInputElement, Animation>();
const appearances = new WeakMap<HTMLInputElement, { backgroundColor: string; borderColor: string }>();

document.addEventListener("DOMContentLoaded", () => initSwitchAnimations());

function initSwitchAnimations(): void {
    document.addEventListener("pointerdown", event => rememberAppearance(event.target));
    document.addEventListener("keydown", event => {
        if (event.key === " ") rememberAppearance(event.target);
    });
    document.addEventListener("change", event => {
        const input = event.target;
        if (!(input instanceof HTMLInputElement) || !input.hasAttribute("data-animated-switch")) return;

        const previous = animations.get(input);
        const start = previous?.playState === "running"
            ? getComputedStyle(input).backgroundPosition
            : input.checked ? "left center" : "right center";
        previous?.cancel();
        const appearance = appearances.get(input);
        appearances.delete(input);
        if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) return;

        const target = getComputedStyle(input);
        const animation = input.animate([
            { backgroundPosition: start, backgroundColor: appearance?.backgroundColor ?? target.backgroundColor,
                borderColor: appearance?.borderColor ?? target.borderColor },
            { backgroundPosition: input.checked ? "right center" : "left center",
                backgroundColor: target.backgroundColor, borderColor: target.borderColor }
        ], { duration: 300, easing: "ease-in-out" });
        animations.set(input, animation);
    });
}

function rememberAppearance(target: EventTarget | null): void {
    if (!(target instanceof Element)) return;
    const label = target.closest("label");
    const input = target instanceof HTMLInputElement ? target : label?.control;
    if (!(input instanceof HTMLInputElement) || !input.hasAttribute("data-animated-switch")) return;

    const style = getComputedStyle(input);
    appearances.set(input, { backgroundColor: style.backgroundColor, borderColor: style.borderColor });
}
