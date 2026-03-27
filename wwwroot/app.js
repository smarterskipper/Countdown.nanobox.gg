window.artVideo = {
    _url: null,
    mount: function (url) {
        if (this._url === url) return;
        this._url = url;

        var existing = document.getElementById('art-bg-video');
        if (existing) existing.remove();
        if (!url) return;

        var v = document.createElement('video');
        v.id = 'art-bg-video';
        v.className = 'art-bg-video';
        v.autoplay = true;
        v.loop = true;
        v.muted = true;
        v.setAttribute('playsinline', '');
        v.style.transition = 'opacity 0.8s ease-in-out';

        // Schedule precise crossfade at loop point using setTimeout
        v.addEventListener('loadedmetadata', function () {
            var fadeOut = 1.0;  // seconds before end to start fade out
            var fadeIn  = 0.3;  // seconds after loop restart to start fade in

            function cycle() {
                if (document.getElementById('art-bg-video') !== v) return;
                var msUntilFade = Math.max(0, (v.duration - fadeOut - v.currentTime) * 1000);

                setTimeout(function () {
                    if (document.getElementById('art-bg-video') !== v) return;
                    v.style.opacity = '0';

                    // Fade back in shortly after the loop restarts
                    setTimeout(function () {
                        if (document.getElementById('art-bg-video') !== v) return;
                        v.style.opacity = '1';
                        // Schedule the next cycle
                        setTimeout(cycle, Math.max(0, (v.duration - fadeOut - fadeIn) * 1000));
                    }, (fadeOut + fadeIn) * 1000);
                }, msUntilFade);
            }

            cycle();
        });

        var s = document.createElement('source');
        s.src = url;
        s.type = 'video/mp4';
        v.appendChild(s);

        var bg = document.querySelector('.art-bg');
        if (bg && bg.parentNode) {
            bg.parentNode.insertBefore(v, bg.nextSibling);
        } else {
            document.body.insertBefore(v, document.body.firstChild);
        }
    },
    unmount: function () {
        var existing = document.getElementById('art-bg-video');
        if (existing) existing.remove();
        this._url = null;
    }
};
