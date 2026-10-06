// Uploads the file with the antiforgery token in a request header, so the server can stream the
// body straight to its temp folder without reading it as a form. Shows upload progress.
(function () {
    'use strict';
    const form = document.getElementById('upload-form');
    if (!form) return;

    const fileInput = form.querySelector('input[type=file]');
    const progress = document.getElementById('upload-progress');
    const errorBox = document.getElementById('upload-error');
    const button = document.getElementById('upload-button');
    const maxBytes = Number(form.dataset.maxBytes);

    function showError(message) {
        errorBox.textContent = message;
        errorBox.hidden = false;
        progress.hidden = true;
        button.disabled = false;
    }

    form.addEventListener('submit', function (event) {
        event.preventDefault();
        errorBox.hidden = true;

        const file = fileInput.files[0];
        if (!file) return showError('Choose a file first.');
        if (file.size > maxBytes) return showError('The file is larger than the upload limit.');

        const data = new FormData();
        data.append('file', file);
        const token = form.querySelector('input[name="__RequestVerificationToken"]').value;

        const request = new XMLHttpRequest();
        request.open('POST', form.action);
        request.setRequestHeader('RequestVerificationToken', token);
        request.upload.onprogress = function (e) {
            if (e.lengthComputable) progress.value = (e.loaded / e.total) * 100;
        };
        request.onload = function () {
            let result = null;
            try { result = JSON.parse(request.responseText); } catch (e) { /* not JSON, e.g. an IIS limit page */ }
            if (result && result.redirect) {
                window.location.href = result.redirect;
            } else {
                showError((result && result.error) ||
                    (request.status === 404 || request.status === 413
                        ? 'The file is larger than the upload limit.'
                        : 'Upload failed (HTTP ' + request.status + ').'));
            }
        };
        request.onerror = function () { showError('Upload failed. Check your connection and the file size.'); };

        button.disabled = true;
        progress.hidden = false;
        progress.value = 0;
        request.send(data);
    });
})();
