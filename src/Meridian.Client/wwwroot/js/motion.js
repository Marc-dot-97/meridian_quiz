/* Meridian functional motion (localhost trial, paired with css/frost.css).
   Principles (after the Audi "Functional Animation" guidance):
   - Show: cards in the viewport rise in bottom-to-top with a short stagger; cards below the fold
     appear only when scrolled into view; every card animates once.
   - Liquid Glass: the specular highlight follows the mouse over glass tiles and controls.
   - Performance: decorative motion pauses while a card is off screen or the tab is hidden;
     weaker devices and small screens get a lighter "lite" tier (set in index.html <head>).
   - Nothing here changes layout or content, and with reduced motion everything shows at once. */
(function () {
    'use strict';
    var root = document.documentElement;
    var reduce = window.matchMedia('(prefers-reduced-motion: reduce)');
    if (!('IntersectionObserver' in window) || !('MutationObserver' in window)) { root.classList.remove('mx-js'); return; }

    // Cards and section headings that take part in the reveal.
    var REVEAL = [
        '.dashboard-section-link', '.arc-card', '.chart-card', '.streak-pill', '.quiz-card', '.empty-completed',
        '.encounter-card', '.survey-card', '.builder-card', '.assign-row', '.block > h2', '.detail-grid > div'
    ].join(',');
    // Cards whose decorative light sweep should pause off screen.
    var SWEEP = '.arc-card, .chart-card, .quiz-card, .empty-completed, .encounter-card';
    var STAGGER_MS = 55, MAX_STEPS = 8;

    var batch = [];
    var flushQueued = false;
    function flush() {
        flushQueued = false;
        // Reading order: top to bottom, then left to right.
        batch.sort(function (a, b) {
            var ra = a.getBoundingClientRect(), rb = b.getBoundingClientRect();
            return (ra.top - rb.top) || (ra.left - rb.left);
        });
        batch.forEach(function (el, i) {
            el.style.setProperty('--mx-delay', (Math.min(i, MAX_STEPS) * STAGGER_MS) + 'ms');
            el.classList.add('mx-in');
        });
        batch = [];
    }

    var revealer = new IntersectionObserver(function (entries) {
        entries.forEach(function (e) {
            if (!e.isIntersecting) return;
            revealer.unobserve(e.target);           // animate once only
            batch.push(e.target);
        });
        if (batch.length && !flushQueued) { flushQueued = true; requestAnimationFrame(flush); }
    }, { rootMargin: '0px 0px -6% 0px', threshold: 0.01 });

    var sweeper = new IntersectionObserver(function (entries) {
        entries.forEach(function (e) { e.target.classList.toggle('mx-offscreen', !e.isIntersecting); });
    }, { rootMargin: '120px 0px' });

    function done(e) {
        var el = e.currentTarget;
        if (e.target !== el) return;
        el.classList.remove('mx-reveal', 'mx-in');
        el.style.removeProperty('--mx-delay');
        el.removeEventListener('animationend', done);
    }

    function enrol(el) {
        if (el.__mx) return;
        el.__mx = true;
        if (el.matches(SWEEP)) sweeper.observe(el);
        if (reduce.matches || el.closest('.loading-panel')) return;
        el.classList.add('mx-reveal');
        el.addEventListener('animationend', done);
        revealer.observe(el);
    }

    function scan(node) {
        if (node.nodeType !== 1) return;
        if (node.matches(REVEAL) || node.matches(SWEEP)) enrol(node);
        var list = node.querySelectorAll(REVEAL + ',' + SWEEP);
        for (var i = 0; i < list.length; i++) enrol(list[i]);
    }

    // Blazor renders and re-renders pages: pick up new cards as they arrive (before they paint).
    new MutationObserver(function (records) {
        for (var r = 0; r < records.length; r++) {
            var added = records[r].addedNodes;
            for (var i = 0; i < added.length; i++) scan(added[i]);
        }
    }).observe(document.body, { childList: true, subtree: true });
    scan(document.body);

    // Liquid Glass: the specular highlight follows the pointer across glass tiles and controls.
    // Mouse/trackpad only; one style write per frame; the light eases back to rest on leave.
    var GLASS = '.arc-card, .chart-card, .quiz-card, .empty-completed, .encounter-card, .btn-arc, .quiz-start, .streak-pill, .bottom-nav';
    var fine = window.matchMedia('(hover: hover) and (pointer: fine)');
    var lit = null, px = 0, py = 0, lightQueued = false;
    function light() {
        lightQueued = false;
        if (!lit) return;
        var r = lit.getBoundingClientRect();
        if (!r.width || !r.height) return;
        lit.style.setProperty('--lg-x', Math.max(0, Math.min(100, (px - r.left) / r.width * 100)).toFixed(1) + '%');
        lit.style.setProperty('--lg-y', Math.max(0, Math.min(100, (py - r.top) / r.height * 100)).toFixed(1) + '%');
    }
    document.addEventListener('pointermove', function (e) {
        if (e.pointerType !== 'mouse' || reduce.matches || !fine.matches || root.classList.contains('mx-lite')) return;
        var t = e.target.closest ? e.target.closest(GLASS) : null;
        if (t !== lit) {
            if (lit) { lit.style.removeProperty('--lg-x'); lit.style.removeProperty('--lg-y'); }
            lit = t;
        }
        if (!lit) return;
        px = e.clientX; py = e.clientY;
        if (!lightQueued) { lightQueued = true; requestAnimationFrame(light); }
    }, { passive: true });
    document.addEventListener('pointerleave', function () {
        if (lit) { lit.style.removeProperty('--lg-x'); lit.style.removeProperty('--lg-y'); lit = null; }
    });

    // Pause all decorative motion while the tab is in the background.
    document.addEventListener('visibilitychange', function () {
        root.classList.toggle('mx-paused', document.hidden);
    });
})();
