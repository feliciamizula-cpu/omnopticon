window.ArgusGrid = {
    _instances: {},

    create: function (id, options) {
        if (this._instances[id]) {
            this._instances[id].destroy();
            delete this._instances[id];
        }

        var gridDiv = document.getElementById(id);
        if (!gridDiv) {
            console.error('ArgusGrid: element #' + id + ' not found');
            return null;
        }

        var gridOptions = {
            rowHeight: options.rowHeight || 28,
            headerHeight: options.headerHeight || 32,
            animateRows: false,
            suppressCellFocus: true,
            rowSelection: { mode: 'singleRow' },
            enableCellTextSelection: true,
            suppressMovableColumns: true,
        };

        if (options.columnDefs) {
            gridOptions.columnDefs = options.columnDefs;
        }
        if (options.rowData) {
            gridOptions.rowData = options.rowData;
        }

        if (options.onRowClicked) {
            gridOptions.onRowClicked = options.onRowClicked;
        }
        if (options.onRowDoubleClicked) {
            gridOptions.onRowDoubleClicked = options.onRowDoubleClicked;
        }
        if (options.onCellContextMenu) {
            gridOptions.onCellContextMenu = options.onCellContextMenu;
        }
        if (options.onSelectionChanged) {
            gridOptions.onSelectionChanged = options.onSelectionChanged;
        }

        if (typeof agGrid !== 'undefined') {
            this._instances[id] = agGrid.createGrid(gridDiv, gridOptions);
        } else if (typeof ag_grid !== 'undefined') {
            this._instances[id] = ag_grid.createGrid(gridDiv, gridOptions);
        } else {
            console.error('ArgusGrid: AG Grid library not found');
            return null;
        }

        return this._instances[id];
    },

    destroy: function (id) {
        if (this._instances[id]) {
            this._instances[id].destroy();
            delete this._instances[id];
        }
    },

    setRowData: function (id, data) {
        if (this._instances[id]) {
            this._instances[id].setGridOption('rowData', data);
        }
    },

    setColumnDefs: function (id, cols) {
        if (this._instances[id]) {
            this._instances[id].setGridOption('columnDefs', cols);
        }
    },

    getSelectedRow: function (id) {
        if (!this._instances[id]) return null;
        var rows = this._instances[id].getSelectedRows();
        return (rows && rows.length > 0) ? rows[0] : null;
    },

    refreshCells: function (id) {
        if (this._instances[id]) {
            this._instances[id].refreshCells();
        }
    },

    sizeColumnsToFit: function (id) {
        if (this._instances[id]) {
            this._instances[id].sizeColumnsToFit();
        }
    }
};