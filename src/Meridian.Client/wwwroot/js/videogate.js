/* Meridian "watch first" video (quiz details page).
   - Remembers the furthest point watched; seeking past it jumps back (pause and rewind are fine).
   - Playback speed is held at 1x until the video has been watched once; after that it can be replayed and skipped freely.
   - No right-click menu (no "Save video as"); the download button is hidden with controlslist="nodownload".
   - Reports progress to Blazor every 10 s, on pause and at the end; the server re-checks against real elapsed time. */
window.meridianVideo = {
    attach: function (el, dotnet, max, completed) {
        if (!el || el.__mv) return;
        var st = { max: Number(max) || 0, completed: !!completed, lastSent: 0 };
        el.__mv = st;
        el.addEventListener('contextmenu', function (e) { e.preventDefault(); });
        function report(ended) {
            st.lastSent = Date.now();
            return dotnet.invokeMethodAsync('OnVideoProgress', st.max, el.duration || 0, !!ended)
                .then(function (r) { if (r && r.completed) st.completed = true; })
                .catch(function () { });
        }
        el.addEventListener('loadedmetadata', function () {
            if (!st.completed && st.max > 2 && st.max < el.duration - 3) el.currentTime = st.max;   // resume where they left off
        });
        el.addEventListener('timeupdate', function () {
            if (el.seeking) return;
            if (el.currentTime <= st.max + 2) st.max = Math.max(st.max, el.currentTime);
            if (!el.paused && Date.now() - st.lastSent > 10000) report(false);
        });
        el.addEventListener('seeking', function () {
            if (!st.completed && el.currentTime > st.max + 1) el.currentTime = st.max;
        });
        el.addEventListener('ratechange', function () {
            if (!st.completed && el.playbackRate > 1) el.playbackRate = 1;
        });
        el.addEventListener('pause', function () { if (!el.ended) report(false); });
        el.addEventListener('ended', function () { st.max = Math.max(st.max, el.duration || 0); report(true); });
    }
};
