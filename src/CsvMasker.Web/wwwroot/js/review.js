// Shows only the options that apply to each column's chosen strategy. Without JS every option is
// visible and still works; the server only uses the ones for the chosen strategy.
(function () {
    'use strict';
    function update(row) {
        const select = row.querySelector('[data-strategy-select]');
        const strategy = select.options[select.selectedIndex].text;
        row.querySelectorAll('.opt').forEach(function (opt) {
            opt.hidden = opt.dataset.for !== strategy;
        });
    }
    document.querySelectorAll('[data-column-row]').forEach(function (row) {
        update(row);
        row.querySelector('[data-strategy-select]').addEventListener('change', function () { update(row); });
    });
})();
