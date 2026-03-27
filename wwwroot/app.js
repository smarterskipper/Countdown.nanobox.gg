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

        // Crossfade at loop point so the cut is invisible
        var fadeDur = 0.9; // seconds to fade out before loop
        var nearEnd = false;
        v.style.transition = 'opacity 0.8s ease-in-out';

        v.addEventListener('timeupdate', function () {
            if (!v.duration) return;
            var timeLeft = v.duration - v.currentTime;

            if (timeLeft < fadeDur && !nearEnd) {
                nearEnd = true;
                v.style.opacity = '0';
            } else if (timeLeft >= fadeDur && nearEnd) {
                // Video has looped back to start
                nearEnd = false;
                v.style.opacity = '1';
            }
        });

        var s = document.createElement('source');
        s.src = url;
        s.type = 'video/mp4';
        v.appendChild(s);

        // Insert after art-bg div so it layers on top
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
