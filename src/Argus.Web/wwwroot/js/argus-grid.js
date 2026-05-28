window.ArgusGrid = {
    _instances: {},

    create: function (id, dotnetRef, options) {
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
            columnDefs: options.columnDefs || [],
            rowData: options.rowData || [],
        };

        if (typeof agGrid !== 'undefined') {
            this._instances[id] = agGrid.createGrid(gridDiv, gridOptions);
        } else if (typeof ag_grid !== 'undefined') {
            this._instances[id] = ag_grid.createGrid(gridDiv, gridOptions);
        } else {
            console.error('ArgusGrid: AG Grid library not found');
            return null;
        }

        var self = this;
        var api = this._instances[id];

        api.addEventListener('rowClicked', function(event) {
            if (event.data && dotnetRef) {
                dotnetRef.invokeMethodAsync('OnGridRowClicked', event.data);
            }
        });

        api.addEventListener('rowDoubleClicked', function(event) {
            if (event.data && dotnetRef) {
                dotnetRef.invokeMethodAsync('OnGridRowDoubleClicked', event.data);
            }
        });

        api.addEventListener('cellContextMenu', function(event) {
            if (event.data && event.event && dotnetRef) {
                dotnetRef.invokeMethodAsync('OnGridCellContextMenu',
                    event.event.clientX,
                    event.event.clientY,
                    event.data
                );
            }
        });

        api.addEventListener('selectionChanged', function(event) {
            var selected = event.api.getSelectedRows();
            if (selected && selected.length > 0 && dotnetRef) {
                dotnetRef.invokeMethodAsync('OnGridSelectionChanged', selected[0]);
            }
        });

        return api;
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