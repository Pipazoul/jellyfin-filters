/*
 * Duration Filter - jellyfin-web client script.
 *
 * Adds a Min/Max runtime filter (in minutes) to the library filter panel.
 *
 * Because the Jellyfin server `/Items` endpoint has no runtime query parameter,
 * filtering is done in the browser: when a duration filter is active we intercept
 * the library's `/Items` request, fetch the *full* result set (every other filter
 * and the sort order still applied by the server), drop items outside the runtime
 * range, then re-paginate locally so item counts and paging stay correct.
 *
 * Everything here is wrapped in try/catch: jellyfin-web internals are not a stable
 * API, so a failure must only disable this feature, never break the page.
 */
(function () {
    'use strict';

    if (window.__durationFilterLoaded) {
        return;
    }
    window.__durationFilterLoaded = true;

    // ----------------------------------------------------------------- config
    var CFG = window.__JELLYFIN_DURATION_FILTER__ || {};
    var TICKS_PER_MINUTE = 600000000; // 1 minute = 600,000,000 ticks (1 tick = 100 ns)
    var STORAGE_PREFIX = 'jf-duration-filter:';
    var CACHE_TTL_MS = 30000;
    var LOG = '[DurationFilter]';

    function log() {
        try {
            console.debug.apply(console, [LOG].concat([].slice.call(arguments)));
        } catch (e) { /* ignore */ }
    }

    function warn() {
        try {
            console.warn.apply(console, [LOG].concat([].slice.call(arguments)));
        } catch (e) { /* ignore */ }
    }

    /**
     * The single tick-conversion helper.
     * @param {number} m Minutes.
     * @returns {number} Equivalent value in Jellyfin ticks.
     */
    function minutesToTicks(m) {
        return m * TICKS_PER_MINUTE;
    }

    // --------------------------------------------------------------- utilities
    function normId(id) {
        return String(id || '').replace(/-/g, '').toLowerCase();
    }

    var enabledLibs = (CFG.enabledLibraryIds || []).map(normId).filter(Boolean);

    function libraryEnabled(libId) {
        if (!enabledLibs.length) {
            return true; // empty config => every library
        }
        return enabledLibs.indexOf(normId(libId)) !== -1;
    }

    function toInt(value) {
        var n = parseInt(value, 10);
        return isNaN(n) || n < 0 ? 0 : n;
    }

    // ---------------------------------------------------- per-library storage
    function getFilter(libId) {
        if (!libId) {
            return null;
        }
        try {
            var raw = localStorage.getItem(STORAGE_PREFIX + normId(libId));
            if (!raw) {
                return null;
            }
            var obj = JSON.parse(raw);
            var min = toInt(obj.min);
            var max = toInt(obj.max);
            if (min <= 0 && max <= 0) {
                return null;
            }
            return { min: min, max: max };
        } catch (e) {
            return null;
        }
    }

    function setFilter(libId, min, max) {
        if (!libId) {
            return;
        }
        try {
            min = toInt(min);
            max = toInt(max);
            var key = STORAGE_PREFIX + normId(libId);
            if (min <= 0 && max <= 0) {
                localStorage.removeItem(key);
            } else {
                localStorage.setItem(key, JSON.stringify({ min: min, max: max }));
            }
        } catch (e) {
            warn('could not persist filter', e);
        }
    }

    function clearFilter(libId) {
        setFilter(libId, 0, 0);
    }

    /** Reads the library id of the view currently shown (from the URL hash). */
    function currentLibraryId() {
        try {
            var hash = window.location.hash || '';
            var qIndex = hash.indexOf('?');
            if (qIndex < 0) {
                return null;
            }
            var params = new URLSearchParams(hash.substring(qIndex + 1));
            return params.get('topParentId') || params.get('parentId') || null;
        } catch (e) {
            return null;
        }
    }

    /**
     * Refreshes the current library grid after a duration filter changes.
     *
     * Preferred: re-query the view *in place* so the active library tab, the
     * page route and the open filter panel are all preserved. jellyfin-web has
     * no public "reload items" call, but its filter dialog reloads the grid
     * whenever one of its own filter controls fires a `change` event. We
     * dispatch `change` on a control whose change handler is a no-op while its
     * state is unchanged (`.chk3DFilter`/`.chk4KFilter` set `x ? true : null`,
     * `.chkStandardFilter` rebuilds an identical list), so jellyfin-web re-runs
     * the query with *its* filters untouched and our fetch/XHR hook layers the
     * duration filter on top.
     *
     * Fallback: a full page reload - used when no filter panel is open (e.g.
     * clearing from the chip). It works but resets the library to its first tab.
     */
    function reloadView() {
        if (requeryInPlace()) {
            return;
        }
        try {
            window.location.reload();
        } catch (e) {
            warn('reload failed', e);
        }
    }

    /**
     * Tries to reload just the library grid through jellyfin-web's own filter
     * machinery, leaving the active tab and route intact.
     * @returns {boolean} True on success; false if the caller should reload.
     */
    function requeryInPlace() {
        try {
            var dialog = document.querySelector('.filterDialog');
            if (!dialog) {
                return false; // no panel open - nothing to drive the re-query
            }
            // These controls map their checkbox state straight onto jellyfin's
            // query, so dispatching `change` without flipping `checked` leaves
            // that query identical - jellyfin-web simply re-runs it.
            var trigger = dialog.querySelector('.chk3DFilter, .chk4KFilter, .chkStandardFilter');
            if (!trigger) {
                return false;
            }
            trigger.dispatchEvent(new Event('change', { bubbles: true }));
            log('re-queried current view in place');
            return true;
        } catch (e) {
            warn('in-place re-query failed, falling back to reload', e);
            return false;
        }
    }

    // ============================================================ re-paging
    //
    // originalFetch is captured before we replace window.fetch, so our own
    // "fetch the full set" request never recurses back into the hook.
    var originalFetch = (typeof window.fetch === 'function') ? window.fetch.bind(window) : null;

    // cache: base-url (no StartIndex/Limit) -> { ts, promise<Item[]> }
    var fullSetCache = Object.create(null);

    function parseUrl(url) {
        try {
            return new URL(url, window.location.origin);
        } catch (e) {
            return null;
        }
    }

    /** True for `/Items` and `/Users/{id}/Items` (but not `/Items/Filters`, `/Items/Latest`, ...). */
    function isItemsPath(pathname) {
        return /\/Items\/?$/.test(pathname || '');
    }

    /** True if the request looks like a paginated library-grid query. */
    function isGridQuery(u) {
        var p = u.searchParams;
        return p.has('Limit') && p.has('StartIndex') && p.has('ParentId') && !p.has('Ids');
    }

    /** Fetches every item for `baseUrl` (StartIndex/Limit already removed), cached briefly. */
    function fetchFullSet(baseUrl, headers, credentials) {
        var entry = fullSetCache[baseUrl];
        var now = Date.now();
        if (entry && (now - entry.ts) <= CACHE_TTL_MS) {
            return entry.promise;
        }

        if (!originalFetch) {
            return Promise.reject(new Error('fetch unavailable'));
        }

        var promise = originalFetch(baseUrl, {
            method: 'GET',
            headers: headers,
            credentials: credentials || 'same-origin'
        }).then(function (r) {
            if (!r || !r.ok) {
                throw new Error('full-set request failed: ' + (r && r.status));
            }
            return r.json();
        }).then(function (data) {
            return (data && Array.isArray(data.Items)) ? data.Items : [];
        });

        fullSetCache[baseUrl] = { ts: now, promise: promise };
        // Drop the cache entry if the request fails so the next attempt retries.
        promise.catch(function () {
            if (fullSetCache[baseUrl] && fullSetCache[baseUrl].promise === promise) {
                delete fullSetCache[baseUrl];
            }
        });
        return promise;
    }

    /**
     * Performs the runtime filter + re-pagination for one intercepted `/Items` request.
     * @returns {Promise<object>} A Jellyfin-shaped `{ Items, TotalRecordCount, StartIndex }`.
     */
    function repagedData(u, headers, credentials) {
        var libId = u.searchParams.get('ParentId');
        var filter = getFilter(libId);
        if (!filter) {
            return Promise.reject(new Error('no active filter'));
        }

        var startIndex = toInt(u.searchParams.get('StartIndex'));
        var limitRaw = u.searchParams.get('Limit');
        var limit = limitRaw == null ? null : toInt(limitRaw);

        var fullUrl = new URL(u.toString());
        fullUrl.searchParams.delete('StartIndex');
        fullUrl.searchParams.delete('Limit');
        fullUrl.searchParams.set('EnableTotalRecordCount', 'true');

        var minTicks = filter.min > 0 ? minutesToTicks(filter.min) : null;
        var maxTicks = filter.max > 0 ? minutesToTicks(filter.max) : null;

        return fetchFullSet(fullUrl.toString(), headers, credentials).then(function (allItems) {
            var filtered = allItems.filter(function (it) {
                var rt = it && typeof it.RunTimeTicks === 'number' ? it.RunTimeTicks : null;
                if (rt == null) {
                    return false; // unknown runtime is excluded while a filter is active
                }
                if (minTicks != null && rt < minTicks) {
                    return false;
                }
                if (maxTicks != null && rt > maxTicks) {
                    return false;
                }
                return true;
            });

            var pageItems = (limit == null)
                ? filtered.slice(startIndex)
                : filtered.slice(startIndex, startIndex + limit);

            log('re-paged', libId, '-', filtered.length, 'of', allItems.length,
                'items match', filter, '(page', startIndex + '..' + (startIndex + pageItems.length) + ')');

            return {
                Items: pageItems,
                TotalRecordCount: filtered.length,
                StartIndex: startIndex
            };
        });
    }

    // ------------------------------------------------------------- fetch hook
    function describeRequest(input, init) {
        var method = 'GET';
        var urlStr;
        var headers;
        var credentials;

        if (typeof input === 'string') {
            urlStr = input;
            method = (init && init.method) || 'GET';
            headers = init && init.headers;
            credentials = init && init.credentials;
        } else if (input && typeof input === 'object') {
            // Request object
            urlStr = input.url;
            method = (init && init.method) || input.method || 'GET';
            headers = (init && init.headers) || input.headers;
            credentials = (init && init.credentials) || input.credentials;
        } else {
            return null;
        }

        if (String(method).toUpperCase() !== 'GET') {
            return null;
        }
        var u = parseUrl(urlStr);
        if (!u || !isItemsPath(u.pathname) || !isGridQuery(u)) {
            return null;
        }
        var libId = u.searchParams.get('ParentId');
        if (!libraryEnabled(libId) || !getFilter(libId)) {
            return null;
        }
        return { url: u, headers: headers, credentials: credentials };
    }

    function installFetchHook() {
        try {
            if (!originalFetch || (window.fetch && window.fetch.__durationFilterHooked)) {
                return;
            }
            var hooked = function (input, init) {
                var target = null;
                try {
                    target = describeRequest(input, init);
                } catch (e) {
                    warn('fetch analysis failed', e);
                }

                if (!target) {
                    return originalFetch(input, init);
                }

                return repagedData(target.url, target.headers, target.credentials)
                    .then(function (body) {
                        return new Response(JSON.stringify(body), {
                            status: 200,
                            headers: { 'Content-Type': 'application/json' }
                        });
                    })
                    .catch(function (e) {
                        warn('re-paging failed, passing request through', e);
                        return originalFetch(input, init);
                    });
            };
            hooked.__durationFilterHooked = true;
            window.fetch = hooked;
            log('fetch hook installed');
        } catch (e) {
            warn('could not install fetch hook', e);
        }
    }

    // --------------------------------------------------------------- XHR hook
    // jellyfin-web's library grid uses fetch, but axios-based code paths use XHR;
    // hooking both keeps the filter working everywhere. Only target grid requests
    // with an active filter are touched - everything else passes straight through.
    function respondToXhr(xhr, status, text) {
        var parsed = null;
        try {
            parsed = JSON.parse(text);
        } catch (e) { /* leave null */ }

        function define(name, getter) {
            try {
                Object.defineProperty(xhr, name, { configurable: true, get: getter });
            } catch (e) { /* some props may be locked; ignore */ }
        }

        define('readyState', function () { return 4; });
        define('status', function () { return status; });
        define('statusText', function () { return 'OK'; });
        define('responseText', function () { return text; });
        define('responseURL', function () { return xhr.__df_url || ''; });
        define('response', function () {
            return xhr.responseType === 'json' ? parsed : text;
        });

        try {
            if (typeof xhr.onreadystatechange === 'function') {
                xhr.onreadystatechange();
            }
            xhr.dispatchEvent(new Event('readystatechange'));
            xhr.dispatchEvent(new Event('load'));
            xhr.dispatchEvent(new Event('loadend'));
        } catch (e) {
            warn('XHR synthetic dispatch failed', e);
        }
    }

    function installXhrHook() {
        try {
            var XHR = window.XMLHttpRequest;
            if (!XHR || XHR.prototype.__durationFilterHooked) {
                return;
            }
            var origOpen = XHR.prototype.open;
            var origSend = XHR.prototype.send;
            var origSetHeader = XHR.prototype.setRequestHeader;

            XHR.prototype.open = function (method, url) {
                try {
                    this.__df_method = method;
                    this.__df_url = url;
                    this.__df_headers = {};
                } catch (e) { /* ignore */ }
                return origOpen.apply(this, arguments);
            };

            XHR.prototype.setRequestHeader = function (name, value) {
                try {
                    if (this.__df_headers) {
                        this.__df_headers[name] = value;
                    }
                } catch (e) { /* ignore */ }
                return origSetHeader.apply(this, arguments);
            };

            XHR.prototype.send = function (body) {
                var xhr = this;
                try {
                    if (String(xhr.__df_method || 'GET').toUpperCase() === 'GET') {
                        var u = parseUrl(xhr.__df_url);
                        if (u && isItemsPath(u.pathname) && isGridQuery(u)) {
                            var libId = u.searchParams.get('ParentId');
                            if (libraryEnabled(libId) && getFilter(libId)) {
                                var creds = xhr.withCredentials ? 'include' : 'same-origin';
                                repagedData(u, xhr.__df_headers, creds)
                                    .then(function (obj) {
                                        respondToXhr(xhr, 200, JSON.stringify(obj));
                                    })
                                    .catch(function (e) {
                                        warn('XHR re-paging failed, passing through', e);
                                        origSend.call(xhr, body);
                                    });
                                return;
                            }
                        }
                    }
                } catch (e) {
                    warn('XHR hook error, passing through', e);
                }
                return origSend.apply(this, arguments);
            };

            XHR.prototype.__durationFilterHooked = true;
            log('XHR hook installed');
        } catch (e) {
            warn('could not install XHR hook', e);
        }
    }

    // ===================================================== filter-dialog UI
    function buildDialogSection(libId) {
        var existing = getFilter(libId);
        var minVal = existing ? existing.min : toInt(CFG.defaultMin);
        var maxVal = existing ? existing.max : toInt(CFG.defaultMax);

        var section = document.createElement('div');
        section.className = 'df-section';
        section.innerHTML =
            '<h2 class="df-title">Duration (minutes)</h2>'
            + '<div class="df-row">'
            + '<label class="df-field"><span class="df-label">Min</span>'
            + '<input type="number" min="0" step="1" inputmode="numeric" class="df-input df-min" /></label>'
            + '<label class="df-field"><span class="df-label">Max</span>'
            + '<input type="number" min="0" step="1" inputmode="numeric" class="df-input df-max" /></label>'
            + '</div>'
            + '<div class="df-actions">'
            + '<button type="button" class="df-btn df-btn-primary df-apply">Apply</button>'
            + '<button type="button" class="df-btn df-clear">Clear</button>'
            + '</div>'
            + '<div class="df-hint">0 = no limit. Composes with the filters below and the current sort order.</div>';

        var minInput = section.querySelector('.df-min');
        var maxInput = section.querySelector('.df-max');
        minInput.value = minVal > 0 ? minVal : '';
        maxInput.value = maxVal > 0 ? maxVal : '';

        function apply() {
            var mn = toInt(minInput.value);
            var mx = toInt(maxInput.value);
            if (mn > 0 && mx > 0 && mx < mn) {
                var t = mn; mn = mx; mx = t; // tolerate reversed input
            }
            setFilter(libId, mn, mx);
            reloadView();
        }

        section.querySelector('.df-apply').addEventListener('click', apply);
        section.querySelector('.df-clear').addEventListener('click', function () {
            clearFilter(libId);
            minInput.value = '';
            maxInput.value = '';
            reloadView();
        });
        section.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') {
                e.preventDefault();
                apply();
            }
        });

        return section;
    }

    function injectIntoFilterDialog(dialog) {
        try {
            if (!dialog || dialog.__durationFilterInjected) {
                return;
            }
            var content = dialog.querySelector('.filterDialogContent');
            if (!content) {
                return;
            }
            var libId = currentLibraryId();
            if (!libraryEnabled(libId)) {
                return; // filter disabled for this library
            }
            dialog.__durationFilterInjected = true;
            content.insertBefore(buildDialogSection(libId), content.firstChild);
            log('filter dialog section injected for library', libId);
        } catch (e) {
            warn('filter dialog injection failed', e);
        }
    }

    function watchForFilterDialog() {
        try {
            var observer = new MutationObserver(function (mutations) {
                for (var i = 0; i < mutations.length; i++) {
                    var added = mutations[i].addedNodes;
                    for (var j = 0; j < added.length; j++) {
                        var node = added[j];
                        if (!node || node.nodeType !== 1) {
                            continue;
                        }
                        try {
                            if (node.classList && node.classList.contains('filterDialog')) {
                                injectIntoFilterDialog(node);
                            } else if (typeof node.querySelector === 'function') {
                                var found = node.querySelector('.filterDialog');
                                if (found) {
                                    injectIntoFilterDialog(found);
                                }
                            }
                        } catch (e) {
                            warn('dialog observer node error', e);
                        }
                    }
                }
            });
            observer.observe(document.body, { childList: true, subtree: true });

            // Catch a dialog that is somehow already open.
            var existing = document.querySelector('.filterDialog');
            if (existing) {
                injectIntoFilterDialog(existing);
            }
        } catch (e) {
            warn('could not start filter-dialog observer', e);
        }
    }

    // ================================================================= init
    function init() {
        installFetchHook();
        installXhrHook();

        function onReady() {
            try {
                watchForFilterDialog();
            } catch (e) {
                warn('UI init failed', e);
            }
        }

        if (document.body) {
            onReady();
        } else {
            document.addEventListener('DOMContentLoaded', onReady);
        }

        log('initialised', CFG.version ? 'v' + CFG.version : '', '- libraries:',
            enabledLibs.length ? enabledLibs : 'all');
    }

    try {
        init();
    } catch (e) {
        warn('initialisation failed', e);
    }
})();
