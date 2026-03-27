window.artVideo = {
    _url: null,
    mount: function (url) {
        var existing = document.getElementById('art-bg-video');
        if (this._url === url && existing) return;
        this._url = url;

        if (existing) existing.remove();
        if (!url) return;

        var v = document.createElement('video');
        v.id = 'art-bg-video';
        v.className = 'art-bg-video';
        v.autoplay = true;
        v.loop = true;
        v.muted = true;
        v.setAttribute('playsinline', '');

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
