(() => {
  "use strict";

  // Copy buttons for the commands
  document.querySelectorAll("[data-copy]").forEach(button => {
    button.addEventListener("click", async () => {
      const text = document.getElementById(button.dataset.copy).textContent;
      try {
        await navigator.clipboard.writeText(text);
        button.textContent = "Copied";
      } catch {
        button.textContent = "Select it";
      }
      setTimeout(() => { button.textContent = "Copy"; }, 1600);
    });
  });

  // Guide: highlight the step you're reading in the contents list
  const tocLinks = [...document.querySelectorAll(".toc a[href^='#']")];
  if (tocLinks.length && "IntersectionObserver" in window) {
    const byId = new Map(tocLinks.map(a => [a.getAttribute("href").slice(1), a]));
    const visible = new Set();
    const spy = new IntersectionObserver(entries => {
      for (const entry of entries) {
        if (entry.isIntersecting) visible.add(entry.target.id);
        else visible.delete(entry.target.id);
      }
      const first = [...byId.keys()].find(id => visible.has(id));
      if (!first) return;
      tocLinks.forEach(a => a.classList.toggle("current", a === byId.get(first)));
    }, { rootMargin: "-80px 0px -55% 0px" });
    byId.forEach((_, id) => { const el = document.getElementById(id); if (el) spy.observe(el); });
  }

  // Gentle reveal for groups arriving from below (content stays visible without it)
  const motion = window.matchMedia("(prefers-reduced-motion: reduce)");
  if (motion.matches || !("IntersectionObserver" in window) || !("animate" in Element.prototype)) return;
  const active = new Map();
  const observer = new IntersectionObserver(entries => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      const element = entry.target;
      observer.unobserve(element);
      if (motion.matches || document.hidden || element.contains(document.activeElement) || entry.boundingClientRect.top < 0) continue;
      const animation = element.animate(
        [{ opacity: 0, transform: "translateY(18px)" }, { opacity: 1, transform: "translateY(0)" }],
        { duration: 520, delay: Number(element.dataset.delay || 0), easing: "cubic-bezier(.22, .61, .36, 1)", fill: "backwards" });
      active.set(element, animation);
      animation.onfinish = () => active.delete(element);
    }
  }, { threshold: 0 });
  document.querySelectorAll("[data-reveal]").forEach(element => {
    if (element.getBoundingClientRect().top >= window.innerHeight) observer.observe(element);
  });
  document.addEventListener("focusin", event => {
    if (!(event.target instanceof Element)) return;
    const group = event.target.closest("[data-reveal]");
    if (!group) return;
    observer.unobserve(group);
    const animation = active.get(group);
    if (animation) { animation.cancel(); active.delete(group); }
  });
})();
