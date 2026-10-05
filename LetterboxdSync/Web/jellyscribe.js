/*
 * Helpers shared by the admin page (configPage.html, window.WS) and the user page (userPage.html,
 * window.WSU). Each page mixes this object into its own and supplies what differs: rootId,
 * noActivityText, getJson(path), postJson(path, body), getProgress(), loadOverview(), runSync(),
 * runWatchlist(), saveAccount(), deleteAccount(), verifyAccount() and
 * populateReviewAccountDropdown(). Static content: no user data here.
 */
window.JellyscribeShared = {
    ensureViewport: function () {
        try {
            var vp = document.querySelector('meta[name="viewport"]');
            if (!vp) { vp = document.createElement('meta'); vp.setAttribute('name', 'viewport'); (document.head || document.documentElement).appendChild(vp); }
            if (!/width\s*=\s*device-width/i.test(vp.getAttribute('content') || '')) vp.setAttribute('content', 'width=device-width, initial-scale=1');
        } catch (e) {}
    },

    esc: function (s) { return String(s || '').replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); },
    escAttr: function (s) { return this.esc(s); },
    errorHtml: function (what, retry) {
        return '<span class="ws-red">Couldn\'t load ' + what + '.</span> <button type="button" class="ws-btn" data-retry="' + retry + '">Retry</button>';
    },
    errorRow: function (what, retry) {
        return '<tr><td colspan="5" style="padding:2em;text-align:center;">' + this.errorHtml(what, retry) + '</td></tr>';
    },
    emptyRow: function (msg) {
        return '<tr><td colspan="5" style="padding:2em;text-align:center;" class="ws-muted">' + msg + '</td></tr>';
    },
    errText: function (err) {
        if (!err) return Promise.resolve('unknown error');
        if (err.json) return err.json().then(function (d) { return (d && d.error) || err.statusText || ('HTTP ' + err.status); }, function () { return err.statusText || ('HTTP ' + err.status); });
        return Promise.resolve(err.message || String(err));
    },
    saveFailed: function (err, statusId) {
        var self = this;
        self.errText(err).then(function (msg) {
            document.getElementById(statusId || 'mStatus').innerHTML = '<span class="ws-red">Save failed: ' + self.esc(msg) + '</span>';
        });
    },
    setModalBusy: function (busy) {
        document.getElementById('mSaveBtn').disabled = busy;
        document.getElementById('mDeleteBtn').disabled = busy;
    },

    // The controls both pages share. bodyIds are the history tables whose rows open the review
    // modal and expand episode groups.
    wireCommon: function (bodyIds) {
        var self = this;
        document.getElementById('ovFilters').addEventListener('click', function (e) {
            var c = e.target.closest('.ws-chip'); if (!c) return;
            this.querySelectorAll('.ws-chip').forEach(function (x) { x.classList.remove('on'); }); c.classList.add('on');
            self.ovFilter = c.getAttribute('data-f');
            self.ovPage = 0;
            self.renderOverview();
            self.syncLabels();
        });
        document.getElementById('ovStatusFilters').addEventListener('click', function (e) {
            var c = e.target.closest('.ws-chip'); if (!c) return;
            this.querySelectorAll('.ws-chip').forEach(function (x) { x.classList.remove('on'); }); c.classList.add('on');
            self.ovStatus = c.getAttribute('data-s');
            self.ovPage = 0;
            self.renderOverview();
        });
        document.getElementById('historySearch').addEventListener('input', function () { self.ovQuery = this.value.trim().toLowerCase(); self.ovPage = 0; self.renderOverview(); });
        document.getElementById('historyPrev').addEventListener('click', function () { if ((self.ovPage || 0) > 0) { self.ovPage--; self.renderOverview(); } });
        document.getElementById('historyNext').addEventListener('click', function () { self.ovPage = (self.ovPage || 0) + 1; self.renderOverview(); });
        document.getElementById('runSyncBtn').addEventListener('click', function () { self.runSync(); });
        document.getElementById('runWatchlistBtn').addEventListener('click', function () { self.runWatchlist(); });
        document.getElementById('mCancelBtn').addEventListener('click', function () { document.getElementById('acctModal').classList.remove('show'); });
        document.getElementById('mSaveBtn').addEventListener('click', function () { self.saveAccount(); });
        document.getElementById('mDeleteBtn').addEventListener('click', function () { self.deleteAccount(); });
        document.getElementById('mVerifyBtn').addEventListener('click', function () { self.verifyAccount(); });
        document.getElementById('mSvc').addEventListener('change', function () { self.onSvcChange(); });
        document.getElementById('reviewCancel').addEventListener('click', function () { document.getElementById('reviewModal').classList.remove('show'); });
        document.getElementById('reviewSubmit').addEventListener('click', function () { self.submitReview(); });
        document.getElementById('reviewRewatch').addEventListener('change', function () {
            document.getElementById('rewatchDateRow').style.display = this.checked ? '' : 'none';
        });
        // Close any modal via the close button, the dimmed backdrop, or Escape.
        ['acctModal', 'reviewModal'].forEach(function (id) {
            document.getElementById(id).addEventListener('click', function (e) { if (e.target === this) this.classList.remove('show'); });
        });
        document.querySelectorAll('.ws-modal-x').forEach(function (x) {
            x.addEventListener('click', function () { var ov = this.closest('.ws-modal-ov'); if (ov) ov.classList.remove('show'); });
        });
        // Once per document: both pages re-run their script on every visit.
        if (!window.__jellyscribeEscapeHandler) {
            window.__jellyscribeEscapeHandler = true;
            document.addEventListener('keydown', function (e) {
                if (e.key === 'Escape') document.querySelectorAll('.ws-modal-ov.show').forEach(function (m) { m.classList.remove('show'); });
            });
        }
        document.getElementById(self.rootId).addEventListener('click', function (e) {
            var r = e.target.closest('[data-retry]'); if (r) self[r.getAttribute('data-retry')]();
        });
        bodyIds.forEach(function (id) {
            document.getElementById(id).addEventListener('click', function (e) {
                var g = e.target.closest('.ws-grp-btn');
                if (g) { self.toggleGroup(this, g); return; }
                var b = e.target.closest('.ws-review-btn'); if (!b) return;
                self.openReview(b.getAttribute('data-svc'), parseInt(b.getAttribute('data-tmdb'), 10) || 0, b.getAttribute('data-title'),
                    b.getAttribute('data-slug'), b.getAttribute('data-season'), b.getAttribute('data-episode'));
            });
        });
        document.querySelectorAll('#starRating .ws-star').forEach(function (star) {
            star.addEventListener('click', function (e) {
                var val = parseFloat(this.getAttribute('data-val'));
                if (e.offsetX < this.offsetWidth / 2) val -= 0.5;
                self.paintStars(val);
            });
        });
    },

    /* ===== History ===== */
    renderSparks: function () {
        var self = this, days = 14, now = Date.now(), dayMs = 86400000;
        function build(svc, stroke, fill) {
            var buckets = new Array(days).fill(0);
            self.merged.forEach(function (e) {
                var idx = days - 1 - Math.floor((now - new Date(e.Timestamp).getTime()) / dayMs);
                // Rating pushes (status 5) aren't logged watches; keep them off the trend line.
                if (e._svc === svc && e.Status !== 5 && e.Status !== 'Rated' && idx >= 0 && idx < days) buckets[idx]++;
            });
            var max = Math.max.apply(null, buckets.concat([1])), w = 120, h = 26, step = w / (days - 1);
            var pts = buckets.map(function (v, i) { return [Math.round(i * step), Math.round(h - 2 - (v / max) * (h - 5))]; });
            var line = pts.map(function (p, i) { return (i ? 'L' : 'M') + p[0] + ' ' + p[1]; }).join(' ');
            return '<path d="' + line + '" fill="none" stroke="' + stroke + '" stroke-width="1.6"/>' +
                '<path d="' + line + ' L' + w + ' ' + h + ' L0 ' + h + ' Z" fill="' + fill + '"/>';
        }
        var sf = document.getElementById('sparkFilm'), st = document.getElementById('sparkTv');
        if (sf) sf.innerHTML = build('letterboxd', 'var(--ws-film)', 'var(--ws-film-soft)');
        if (st) st.innerHTML = build('serializd', 'var(--ws-tv)', 'var(--ws-tv-soft)');
    },
    renderOverview: function () {
        var self = this, f = this.ovFilter || 'all', q = this.ovQuery || '';
        var all = this.merged.filter(function (e) {
            return (f === 'all' || e._svc === f) && self.matchesStatus(e) && (!q || (e.FilmTitle || '').toLowerCase().indexOf(q) >= 0);
        });
        var units = this.groupRuns(all);
        var ps = this.ovPageSize || 40, total = units.length, pages = Math.max(1, Math.ceil(total / ps));
        if ((this.ovPage || 0) >= pages) this.ovPage = pages - 1;
        var start = (this.ovPage || 0) * ps;
        var empty = this.merged.length ? 'No items match these filters.' : this.noActivityText;
        document.getElementById('historyBody').innerHTML = units.length ? this.unitsHtml(units.slice(start, start + ps)) : this.emptyRow(empty);
        var pag = document.getElementById('historyPagination');
        if (total > ps) {
            pag.style.display = 'flex';
            document.getElementById('historyPageInfo').textContent = (start + 1) + '–' + Math.min(start + ps, total) + ' of ' + total;
            document.getElementById('historyPrev').disabled = (this.ovPage || 0) === 0;
            document.getElementById('historyNext').disabled = (this.ovPage || 0) >= pages - 1;
        } else { pag.style.display = 'none'; }
    },
    statusOf: function (e) {
        if (e.Source === 'review') return 'Reviewed';
        return typeof e.Status === 'string' ? e.Status : (['Success', 'Skipped', 'Failed', 'Rewatch', 'Requested', 'Rated'][e.Status] || 'Success');
    },
    matchesStatus: function (e) {
        var s = this.ovStatus || 'all';
        if (s === 'all') return true;
        var st = this.statusOf(e);
        if (s === 'hide-skipped') return st !== 'Skipped';
        return st === s;
    },
    whenOf: function (e) {
        var ts = new Date(e.Timestamp);
        return ts.toLocaleDateString() + ' ' + ts.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    },
    pillHtml: function (status, reason) {
        status = this.esc(status);
        if (!reason) return '<span class="ws-pill ' + status + '">' + status + '</span>';
        return '<span class="ws-pill ' + status + ' has-reason" title="' + this.esc(reason) + '">' + status + ' <span class="ws-info-i">i</span></span>';
    },
    // e._svc says which diary the event came from ('letterboxd' or 'serializd').
    rowHtml: function (e, trAttrs) {
        var self = this, svc = e._svc === 'serializd' ? 'tv' : 'film';
        var status = self.statusOf(e), raw = self.statusOf({ Status: e.Status });
        var title = (e.FilmTitle || '').split(' · '), t = title[0], m = title[1] || '';
        var canReview = raw === 'Success' || raw === 'Rewatch' || raw === 'Skipped';
        var seM = (e._svc === 'serializd') ? (m || '').match(/S(\d+)E(\d+)/) : null;
        var btn = (canReview && e.TmdbId) ? '<button type="button" class="ws-review-btn" data-svc="' + self.esc(e._svc) + '" data-tmdb="' + self.esc(e.TmdbId) +
            '" data-title="' + self.esc(t) + '" data-slug="' + self.esc(e.FilmSlug) + '" data-season="' + (seM ? seM[1] : '') + '" data-episode="' + (seM ? seM[2] : '') + '">Review</button>' : '';
        return '<tr' + (trAttrs || '') + '><td><div class="ws-titlecell ws-tc"><span class="ws-badge ' + svc + '">' + (svc === 'tv' ? 'TV' : 'MV') + '</span>' +
            '<div><div class="t">' + self.esc(t) + '</div>' + '<div class="m">' + self.esc(m) + '<span class="ws-mwhen">' + (m ? ' · ' : '') + self.whenOf(e) + '</span></div>' + '</div></div></td>' +
            '<td>' + self.pillHtml(status, e.Error) + '</td>' +
            '<td class="ws-src">' + self.esc(e.Source) + '</td><td class="ws-when">' + self.whenOf(e) + '</td><td>' + btn + '</td></tr>';
    },
    // Consecutive episodes of one show collapse into one expandable row, so a binge
    // doesn't push everything else off the page. A run of one stays a plain row.
    groupRuns: function (events) {
        var units = [], episode = /^S\d+E\d+$/;
        events.forEach(function (e) {
            var parts = (e.FilmTitle || '').split(' · '), last = units[units.length - 1];
            var isEpisode = e._svc === 'serializd' && episode.test(parts[1] || '');
            if (isEpisode && last && last.show === parts[0]) { last.events.push(e); return; }
            units.push({ show: isEpisode ? parts[0] : null, events: [e] });
        });
        return units;
    },
    unitsHtml: function (units) {
        var self = this;
        return units.map(function (u, i) {
            if (u.events.length < 2) return self.rowHtml(u.events[0]);
            var attrs = ' class="ws-grp-item" data-grp-of="' + i + '" hidden';
            return self.groupHtml(u, i) + u.events.map(function (e) { return self.rowHtml(e, attrs); }).join('');
        }).join('');
    },
    groupHtml: function (u, id) {
        var self = this, newest = u.events[0], oldest = u.events[u.events.length - 1];
        var episodeOf = function (e) { return (e.FilmTitle || '').split(' · ')[1]; };
        var uniq = function (fn) { return u.events.map(fn).filter(function (v, i, a) { return a.indexOf(v) === i; }); };
        var statuses = uniq(function (e) { return self.statusOf(e); }), sources = uniq(function (e) { return e.Source || ''; });
        var pill = statuses.length === 1 ? self.pillHtml(statuses[0]) : '<span class="ws-pill Skipped">Mixed</span>';
        return '<tr class="ws-grp"><td><button type="button" class="ws-grp-btn ws-tc" data-grp="' + id + '" aria-expanded="false"><span class="ws-badge tv">TV</span><div><div class="t">' + self.esc(u.show) +
            ' <span class="ws-grp-caret">▸</span></div><div class="m">' + u.events.length + ' episodes · ' + self.esc(episodeOf(oldest)) + '–' + self.esc(episodeOf(newest)) + '<span class="ws-mwhen"> · ' + self.whenOf(newest) + '</span></div></div></button></td><td>' + pill +
            '</td><td class="ws-src">' + (sources.length === 1 ? self.esc(sources[0]) : '') + '</td><td class="ws-when">' + self.whenOf(newest) + '</td><td></td></tr>';
    },
    toggleGroup: function (body, btn) {
        var open = btn.getAttribute('aria-expanded') !== 'true';
        btn.setAttribute('aria-expanded', String(open));
        body.querySelectorAll('[data-grp-of="' + btn.getAttribute('data-grp') + '"]').forEach(function (r) { r.hidden = !open; });
    },

    /* ===== Sync job watcher (honours the All / Film / TV filter) ===== */
    syncLabels: function () {
        var f = this.ovFilter, svc = f === 'serializd' ? 'TV' : (f === 'letterboxd' ? 'film' : 'all');
        document.getElementById('runSyncBtn').textContent = f === 'all' ? 'Sync all now' : 'Sync ' + svc + ' now';
        document.getElementById('runWatchlistBtn').textContent = f === 'all' ? 'Sync all watchlists' : 'Sync ' + svc + ' watchlist';
    },
    startWatcher: function (label) {
        var self = this, w = self.watcher || (self.watcher = {});
        if (w.timer) clearInterval(w.timer);
        w.sawRunning = false; w.ticks = 0;
        document.getElementById('jwTitle').textContent = label + '…';
        document.getElementById('jwPhase').textContent = 'Starting…';
        document.getElementById('jwCounts').textContent = '';
        document.getElementById('jwElapsed').textContent = '';
        var fill = document.getElementById('jwFill'); fill.style.width = '0%'; fill.classList.add('indet');
        document.getElementById('jwSpin').classList.remove('done'); document.getElementById('jwSpin').innerHTML = '';
        document.getElementById('jobWatcher').style.display = 'block';
        document.getElementById('runSyncBtn').disabled = true; document.getElementById('runWatchlistBtn').disabled = true;
        w.timer = setInterval(function () { self.pollProgress(); }, 1000);
        self.pollProgress();
    },
    pollProgress: function () {
        var self = this, w = self.watcher;
        // The page was removed (the in-app page was left): stop polling.
        if (!document.getElementById('jobWatcher')) { clearInterval(w.timer); w.timer = null; return; }
        w.ticks++;
        self.getProgress().then(function (p) {
            if (!p) return;
            if (p.isRunning) {
                w.sawRunning = true;
                document.getElementById('jwTitle').textContent = p.taskName || 'Syncing';
                document.getElementById('jwPhase').textContent = p.phase || '';
                document.getElementById('jwElapsed').textContent = (p.elapsedSeconds || 0) + 's';
                var fill = document.getElementById('jwFill');
                if (p.totalItems > 0) {
                    fill.classList.remove('indet');
                    fill.style.width = Math.min(100, Math.round(p.processedItems / p.totalItems * 100)) + '%';
                    document.getElementById('jwCounts').textContent = p.processedItems + ' / ' + p.totalItems;
                } else { fill.classList.add('indet'); document.getElementById('jwCounts').textContent = ''; }
            } else if (w.sawRunning || w.ticks > 6) {
                self.finishWatcher(w.sawRunning);
            }
        });
    },
    finishWatcher: function (completed) {
        var self = this, w = self.watcher;
        if (w.timer) { clearInterval(w.timer); w.timer = null; }
        var fill = document.getElementById('jwFill'); fill.classList.remove('indet'); fill.style.width = '100%';
        var spin = document.getElementById('jwSpin'); spin.classList.add('done'); spin.innerHTML = completed ? '✓' : '';
        document.getElementById('jwTitle').textContent = completed ? 'Done' : 'Nothing to sync';
        document.getElementById('jwPhase').textContent = completed ? 'Sync complete' : 'Everything was already up to date';
        document.getElementById('runSyncBtn').disabled = false; document.getElementById('runWatchlistBtn').disabled = false;
        setTimeout(function () { document.getElementById('jobWatcher').style.display = 'none'; self.loadOverview(); }, completed ? 2600 : 1600);
    },

    /* ===== Account modal ===== */
    featChip: function (label, on) { return '<span class="ws-feat ' + (on ? 'on' : '') + '">' + label + '</span>'; },
    // Stored ids for libraries this page listed are replaced by what is ticked; ids for libraries
    // it did not list are kept (an admin may have excluded one the user cannot see), so a partial
    // or failed library list can never silently clear an exclusion. That also keeps ids of deleted
    // libraries, which is harmless: they match nothing.
    mergeExcluded: function (stored, ticked) {
        var shown = (this.libraries || []).map(function (l) { return l.id; });
        return stored.filter(function (id) { return shown.indexOf(id) < 0; }).concat(ticked);
    },
    libsFor: function (excluded) {
        var self = this;
        return (self.libraries || []).map(function (l) {
            return '<label><input type="checkbox" class="mLibChk" data-id="' + self.esc(l.id) + '"' + (excluded.indexOf(l.id) >= 0 ? ' checked' : '') + ' /><span>' + self.esc(l.name) + '<span class="desc">' + ({ movies: 'Films', tvshows: 'TV', mixed: 'Mixed' }[l.collectionType] || 'Mixed') + '</span></span></label>';
        }).join('');
    },
    onSvcChange: function () {
        var svc = document.getElementById('mSvc').value;
        document.getElementById('mUserLabel').textContent = svc === 'serializd' ? 'Email or username' : 'Letterboxd username';
        document.getElementById('mAuthBlobRow').style.display = svc === 'letterboxd' ? 'block' : 'none';
        document.getElementById('mUserAgentRow').style.display = svc === 'letterboxd' ? 'block' : 'none';
        var ratingsBox = document.getElementById('chkRatings');
        if (ratingsBox) ratingsBox.closest('label').style.display = svc === 'letterboxd' ? '' : 'none';
        var uname = document.getElementById('mUsername').value.trim();
        document.getElementById('mWatchName').placeholder = svc === 'serializd' ? 'Serializd Watchlist' : ('Letterboxd Watchlist' + (uname ? ' (' + uname + ')' : ''));
    },
    collectModal: function () {
        var g = function (id) { return document.getElementById(id); };
        return {
            svc: g('mSvc').value, userId: g('mUser') ? g('mUser').value : null,
            username: g('mUsername').value.trim(), password: g('mPassword').value, cookies: g('mAuthBlob').value,
            userAgent: g('mUserAgent').value.trim(), watchName: g('mWatchName').value.trim(),
            enabled: g('chkEnabled').checked, fav: g('chkFav').checked, ratings: g('chkRatings') ? g('chkRatings').checked : true, date: g('chkDate').checked, days: parseInt(g('chkDateDays').value, 10) || 7,
            primary: g('chkPrimary').checked, watch: g('chkWatch').checked, seerr: g('chkSeerr').checked,
            backfill: g('chkBackfill').checked, mirror: g('chkMirror').checked,
            skip: g('chkSkip').checked, stop: g('chkStop').checked, imp: g('chkImport').checked,
            libs: Array.prototype.map.call(document.querySelectorAll('#mLibs .mLibChk:checked'), function (x) { return x.getAttribute('data-id'); })
        };
    },
    verifyMessage: function (ok, d) {
        var esc = function (s) { return String(s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); };
        d = d || {};
        if (ok && d.via === 'api') return '<span class="ws-green">Login OK (official API).</span>';
        if (ok) return '<span class="ws-green">Login OK via the website login.</span> <span class="ws-muted">The official API refused it: ' + esc(d.apiError) + '</span>';
        var parts = [];
        if (d.error && d.error !== 'Login failed.') parts.push(esc(d.error));
        if (d.apiError) parts.push('Official API: ' + esc(d.apiError));
        if (d.websiteError) parts.push('Website: ' + esc(d.websiteError));
        return '<span class="ws-red">Login failed.' + (parts.length ? ' ' + parts.join(' · ') : '') + '</span>';
    },

    /* ===== Review modal ===== */
    /* Paint the star widget to a 0.5-step value (null/0 clears). Single
       writer for reviewRating and ratingLabel so click, reset, and
       pre-fill can't disagree. */
    paintStars: function (val) {
        document.getElementById('reviewRating').value = val || '';
        document.getElementById('ratingLabel').textContent = val ? val + ' / 5' : 'No rating';
        document.querySelectorAll('#starRating .ws-star').forEach(function (s) {
            var sv = parseFloat(s.getAttribute('data-val'));
            s.classList.toggle('filled', !!val && sv <= val);
            s.classList.toggle('half', !!val && sv - 0.5 === val);
        });
    },
    openReview: function (svc, tmdbId, title, slug, season, episode) {
        this.reviewSvc = svc; this.reviewTmdb = tmdbId; this.reviewSlug = slug || '';
        this.reviewTitleText = title || '';
        this.reviewSeason = season ? parseInt(season, 10) : null;
        this.reviewEpisode = episode ? parseInt(episode, 10) : null;
        var scope = (this.reviewSeason && this.reviewEpisode) ? (' · S' + this.reviewSeason + 'E' + this.reviewEpisode) : '';
        document.getElementById('reviewTitle').textContent = 'Review: ' + title + scope;
        document.getElementById('reviewText').value = '';
        document.getElementById('reviewSpoilers').checked = false;
        document.getElementById('reviewRewatch').checked = false;
        document.getElementById('rewatchDateRow').style.display = 'none';
        document.getElementById('reviewDate').value = new Date().toISOString().split('T')[0];
        this.paintStars(null);
        document.getElementById('reviewStatus').textContent = '';
        var isLetterboxd = svc !== 'serializd';
        document.getElementById('reviewRewatchRow').style.display = isLetterboxd ? '' : 'none';
        document.getElementById('reviewAccountRow').style.display = 'none';
        if (isLetterboxd) this.populateReviewAccountDropdown();
        document.getElementById('reviewModal').classList.add('show');
        this.prefillRating(svc, tmdbId);
    },
    /* Pre-fill the stars from the rating already stored in Jellyfin.
       Fail-soft by design: any error or null response just leaves
       "No rating", so the modal never blocks on this lookup. The
       sequence token discards a late response if another item's
       modal was opened in the meantime. */
    prefillRating: function (svc, tmdbId) {
        this.reviewOpenSeq = (this.reviewOpenSeq || 0) + 1;
        var seq = this.reviewOpenSeq, self = this;
        if (!tmdbId) return;
        var q = 'Jellyfin.Plugin.LetterboxdSync/ItemRating?tmdbId=' + tmdbId;
        if (svc === 'serializd') {
            q += '&isShow=true';
            if (this.reviewSeason && this.reviewEpisode) q += '&seasonNumber=' + this.reviewSeason + '&episodeNumber=' + this.reviewEpisode;
        }
        this.getJson(q).then(function (d) {
            if (seq === self.reviewOpenSeq && d && d.stars) self.paintStars(d.stars);
        }).catch(function () { });
    },
    submitReview: function () {
        var self = this, text = document.getElementById('reviewText').value.trim();
        var rv = document.getElementById('reviewRating').value, rating = rv ? parseFloat(rv) : null;
        var isRewatch = self.reviewSvc !== 'serializd' && document.getElementById('reviewRewatch').checked;
        if (!text && !rating && !isRewatch) { document.getElementById('reviewStatus').innerHTML = '<span class="ws-red">Write a review, set a rating, or mark as rewatch.</span>'; return; }
        document.getElementById('reviewStatus').textContent = 'Posting…';
        var url, body;
        if (self.reviewSvc === 'serializd') {
            url = 'Jellyfin.Plugin.LetterboxdSync/Serializd/Review';
            body = { tmdbId: self.reviewTmdb, rating: rating ? Math.round(rating * 2) : null, reviewText: text || null, containsSpoilers: document.getElementById('reviewSpoilers').checked, seasonNumber: self.reviewSeason || null, episodeNumber: self.reviewEpisode || null, title: self.reviewTitleText || null };
        } else {
            var date = document.getElementById('reviewDate').value;
            var lbUser = (document.getElementById('reviewAccount').value || '').trim();
            url = 'Jellyfin.Plugin.LetterboxdSync/Review';
            body = { filmSlug: self.reviewSlug, tmdbId: self.reviewTmdb || null, reviewText: text || null, containsSpoilers: document.getElementById('reviewSpoilers').checked, isRewatch: isRewatch, date: isRewatch && date ? date : null, rating: rating, letterboxdUsername: lbUser || null };
        }
        self.postJson(url, body)
            .then(function () { document.getElementById('reviewStatus').innerHTML = '<span class="ws-green">Review posted!</span>'; setTimeout(function () { document.getElementById('reviewModal').classList.remove('show'); }, 1200); },
                function () { document.getElementById('reviewStatus').innerHTML = '<span class="ws-red">Failed to post.</span>'; });
    }
};
