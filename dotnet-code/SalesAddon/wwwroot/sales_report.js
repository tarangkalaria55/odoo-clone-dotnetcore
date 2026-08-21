import React, { useState, useEffect } from 'react';

export function ProfitAndLossReport({ callKw }) {
    const [orders, setOrders] = useState([]);
    const [filterStatus, setFilterStatus] = useState('all');

    useEffect(() => {
        callKw('sale.order', 'search_read', [], { fields: ['name', 'partner_id', 'amount_untaxed', 'total_margin', 'status'] })
            .then(data => setOrders(data));
    }, []);

    const filteredOrders = orders.filter(o => filterStatus === 'all' || o.status === filterStatus);

    const totalRevenue = filteredOrders.reduce((sum, o) => sum + (parseFloat(o.amount_untaxed) || 0), 0);
    const totalProfit = filteredOrders.reduce((sum, o) => sum + (parseFloat(o.total_margin) || 0), 0);
    const totalCogs = totalRevenue - totalProfit;
    const netMarginPercent = totalRevenue > 0 ? ((totalProfit / totalRevenue) * 100).toFixed(1) : 0;

    return React.createElement('div', { className: 'container-fluid px-4 py-3' },
        React.createElement('div', { className: 'd-flex justify-content-between align-items-center mb-4' },
            React.createElement('h3', { className: 'fw-bold text-secondary m-0' }, '📊 Profit & Loss Financial Statement'),
            React.createElement('div', { className: 'btn-group' },
                React.createElement('button', { className: `btn btn-sm ${filterStatus === 'all' ? 'btn-secondary' : 'btn-outline-secondary'}`, onClick: () => setFilterStatus('all') }, 'All Orders'),
                React.createElement('button', { className: `btn btn-sm ${filterStatus === 'sale' ? 'btn-secondary' : 'btn-outline-secondary'}`, onClick: () => setFilterStatus('sale') }, 'Confirmed Sales'),
                React.createElement('button', { className: `btn btn-sm ${filterStatus === 'draft' ? 'btn-secondary' : 'btn-outline-secondary'}`, onClick: () => setFilterStatus('draft') }, 'Quotations')
            )
        ),
        React.createElement('div', { className: 'row g-3 mb-4' },
            React.createElement('div', { className: 'col-md-3' },
                React.createElement('div', { className: 'card shadow-sm border-0 bg-primary text-white p-3 rounded-3' },
                    React.createElement('div', { className: 'small text-white-50' }, 'Gross Operating Revenue'),
                    React.createElement('div', { className: 'fs-3 fw-bold' }, `$${totalRevenue.toLocaleString()}`)
                )
            ),
            React.createElement('div', { className: 'col-md-3' },
                React.createElement('div', { className: 'card shadow-sm border-0 bg-warning text-dark p-3 rounded-3' },
                    React.createElement('div', { className: 'small text-black-50' }, 'Cost of Goods Sold (COGS)'),
                    React.createElement('div', { className: 'fs-3 fw-bold' }, `$${totalCogs.toLocaleString()}`)
                )
            ),
            React.createElement('div', { className: 'col-md-3' },
                React.createElement('div', { className: 'card shadow-sm border-0 bg-success text-white p-3 rounded-3' },
                    React.createElement('div', { className: 'small text-white-50' }, 'Net Operating Profit'),
                    React.createElement('div', { className: 'fs-3 fw-bold' }, `$${totalProfit.toLocaleString()}`)
                )
            ),
            React.createElement('div', { className: 'col-md-3' },
                React.createElement('div', { className: 'card shadow-sm border-0 bg-dark text-white p-3 rounded-3' },
                    React.createElement('div', { className: 'small text-white-50' }, 'Net Margin %'),
                    React.createElement('div', { className: 'fs-3 fw-bold' }, `${netMarginPercent}%`)
                )
            )
        ),
        React.createElement('div', { className: 'card shadow-sm border-0 p-4' },
            React.createElement('h5', { className: 'fw-bold mb-3' }, 'Statement Line Items'),
            React.createElement('div', { className: 'table-responsive' },
                React.createElement('table', { className: 'table table-hover align-middle mb-0' },
                    React.createElement('thead', { className: 'table-light' },
                        React.createElement('tr', null,
                            React.createElement('th', null, 'Order #'),
                            React.createElement('th', null, 'Customer'),
                            React.createElement('th', null, 'Status'),
                            React.createElement('th', { className: 'text-end' }, 'Revenue'),
                            React.createElement('th', { className: 'text-end' }, 'Profit Margin')
                        )
                    ),
                    React.createElement('tbody', null,
                        filteredOrders.map(o => React.createElement('tr', { key: o.id },
                            React.createElement('td', { className: 'fw-bold' }, o.name),
                            React.createElement('td', null, Array.isArray(o.partner_id) ? o.partner_id[1] : o.partner_id),
                            React.createElement('td', null, React.createElement('span', { className: 'badge bg-light text-dark border' }, o.status)),
                            React.createElement('td', { className: 'text-end' }, `$${parseFloat(o.amount_untaxed || 0).toLocaleString()}`),
                            React.createElement('td', { className: 'text-end text-success fw-bold' }, `$${parseFloat(o.total_margin || 0).toLocaleString()}`)
                        ))
                    )
                )
            )
        )
    );
}

export default ProfitAndLossReport;
