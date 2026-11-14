/* ThixClip.Cloud: the few behaviours only this app has (the toolkit, matui.js, does the rest).
   - "In Zwischenablage kopieren" of a picture (data-copy-image="/live/clips/1/image"): the PNG goes into the clipboard as an image. A text is copied by the
     toolkit (data-copy). */
(function () {
  'use strict';

  function say(message, tone) {
    if (window.MatUi && window.MatUi.toast) { window.MatUi.toast(message, { tone: tone }); }
  }

  document.addEventListener('click', function (e) {
    var button = e.target.closest && e.target.closest('[data-copy-image]');
    if (!button) { return; }
    e.preventDefault();
    if (!navigator.clipboard || !window.ClipboardItem) {
      say('Dieser Browser kann keine Bilder in die Zwischenablage legen. Bitte „Herunterladen“ verwenden.', 'warning');
      return;
    }

    var url = button.getAttribute('data-copy-image');
    // Safari needs the promise inside the ClipboardItem (the click must still be the gesture); the others take it as well.
    var blob = fetch(url, { credentials: 'same-origin' }).then(function (r) {
      if (!r.ok) { throw new Error('HTTP ' + r.status); }
      return r.blob();
    }).then(function (b) { return b.type === 'image/png' ? b : new Blob([b], { type: 'image/png' }); });
    navigator.clipboard.write([new ClipboardItem({ 'image/png': blob })])
      .then(function () { say('Bild in die Zwischenablage kopiert.', 'success'); })
      .catch(function () { say('Das Bild konnte nicht kopiert werden.', 'danger'); });
  });
})();
