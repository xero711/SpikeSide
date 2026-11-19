/* SpikeSide — progressive enhancements (no dependencies) */
(function () {
  'use strict';

  var root = document.documentElement;
  root.classList.add('js');

  var reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)');

  /* ---------- Header: scrolled state ---------- */
  var header = document.querySelector('[data-header]');
  if (header) {
    var onScroll = function () {
      header.classList.toggle('is-scrolled', window.scrollY > 8);
    };
    onScroll();
    window.addEventListener('scroll', onScroll, { passive: true });
  }

  /* ---------- Mobile navigation ---------- */
  var toggle = document.querySelector('[data-nav-toggle]');
  var nav = document.getElementById('site-nav');
  var desktop = window.matchMedia('(min-width: 60.0625rem)');

  if (header && toggle && nav) {
    var isOpen = function () {
      return toggle.getAttribute('aria-expanded') === 'true';
    };

    var setOpen = function (open, returnFocus) {
      toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
      header.classList.toggle('is-open', open);
      if (!open && returnFocus) toggle.focus();
    };

    toggle.addEventListener('click', function () {
      setOpen(!isOpen(), false);
    });

    nav.addEventListener('click', function (event) {
      if (event.target.closest('a')) setOpen(false, false);
    });

    document.addEventListener('keydown', function (event) {
      if (event.key === 'Escape' && isOpen()) setOpen(false, true);
    });

    document.addEventListener('click', function (event) {
      if (isOpen() && !header.contains(event.target)) setOpen(false, false);
    });

    header.addEventListener('focusout', function (event) {
      if (isOpen() && event.relatedTarget && !header.contains(event.relatedTarget)) {
        setOpen(false, false);
      }
    });

    var onBreakpoint = function (event) {
      if (event.matches) setOpen(false, false);
    };
    if (desktop.addEventListener) desktop.addEventListener('change', onBreakpoint);
    else if (desktop.addListener) desktop.addListener(onBreakpoint);
  }

  /* ---------- Screens: accessible tabs ---------- */
  var tabsRoot = document.querySelector('[data-tabs]');
  if (tabsRoot) {
    var tablist = tabsRoot.querySelector('[data-tablist]');
    var tabs = Array.prototype.slice.call(tabsRoot.querySelectorAll('[data-tab]'));
    var panels = tabs.map(function (tab) {
      return document.getElementById(tab.getAttribute('aria-controls'));
    });

    if (tablist && tabs.length && panels.every(Boolean)) {
      tablist.setAttribute('role', 'tablist');
      tablist.hidden = false;

      tabs.forEach(function (tab, i) {
        tab.setAttribute('role', 'tab');
        panels[i].setAttribute('role', 'tabpanel');
        panels[i].setAttribute('aria-labelledby', tab.id);
        panels[i].setAttribute('tabindex', '0');
      });

      var select = function (index, moveFocus) {
        tabs.forEach(function (tab, i) {
          var active = i === index;
          tab.setAttribute('aria-selected', active ? 'true' : 'false');
          tab.setAttribute('tabindex', active ? '0' : '-1');
          panels[i].hidden = !active;
        });
        if (moveFocus) tabs[index].focus();
      };

      tabs.forEach(function (tab, i) {
        tab.addEventListener('click', function () {
          select(i, false);
        });
        tab.addEventListener('keydown', function (event) {
          var next = null;
          switch (event.key) {
            case 'ArrowRight': next = (i + 1) % tabs.length; break;
            case 'ArrowLeft': next = (i - 1 + tabs.length) % tabs.length; break;
            case 'Home': next = 0; break;
            case 'End': next = tabs.length - 1; break;
            default: return;
          }
          event.preventDefault();
          select(next, true);
        });
      });

      select(0, false);
    }
  }

  /* ---------- Active section in navigation ---------- */
  var navLinks = Array.prototype.slice.call(document.querySelectorAll('.site-nav__list a[href^="#"]'));
  if ('IntersectionObserver' in window && navLinks.length) {
    var linkById = {};
    navLinks.forEach(function (link) {
      linkById[link.getAttribute('href').slice(1)] = link;
    });

    var sectionObserver = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) return;
        navLinks.forEach(function (link) { link.classList.remove('is-active'); });
        var link = linkById[entry.target.id];
        if (link) link.classList.add('is-active');
      });
    }, { rootMargin: '-45% 0px -50% 0px' });

    document.querySelectorAll('main section[id]').forEach(function (section) {
      sectionObserver.observe(section);
    });
  }

  /* ---------- Reveal on scroll (only below the fold, never with reduced motion) ---------- */
  var revealItems = document.querySelectorAll('[data-reveal]');
  if (!reduceMotion.matches && 'IntersectionObserver' in window && revealItems.length) {
    var revealObserver = new IntersectionObserver(function (entries, observer) {
      entries.forEach(function (entry) {
        if (entry.isIntersecting) {
          entry.target.classList.remove('is-pending');
          observer.unobserve(entry.target);
        }
      });
    }, { rootMargin: '0px 0px -8% 0px', threshold: 0.05 });

    var fold = window.innerHeight * 0.92;
    revealItems.forEach(function (el) {
      if (el.getBoundingClientRect().top > fold) {
        el.classList.add('is-pending');
        revealObserver.observe(el);
      }
    });

    // Never leave content hidden if the user jumps via anchor or find-in-page.
    window.addEventListener('hashchange', function () {
      revealItems.forEach(function (el) { el.classList.remove('is-pending'); });
    });
  }

  /* ---------- Footer year ---------- */
  var year = document.querySelector('[data-year]');
  if (year) year.textContent = String(new Date().getFullYear());
})();
