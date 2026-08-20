import React, { useState, useEffect } from 'react';
import { createRoot } from 'react-dom/client';

async function callKw(model, method, args = [], kwargs = {}) {
    const res = await fetch('/web/dataset/call_kw', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ model, method, args, kwargs })
    });
    const data = await res.json();
    if (!res.ok) throw new Error(data.error || 'Server error occurred');
    return data;
}

function evalModifier(expr, record) {
    if (!expr) return false;
    try {
        const keys = Object.keys(record);
        const vals = Object.values(record);
        return new Function(...keys, `return Boolean(${expr});`)(...vals);
    } catch { return false; }
}

export function AppsManagerDashboard({ onAppToggled }) {
    const [apps, setApps] = useState([]);
    const [loading, setLoading] = useState(false);

    const loadApps = () => {
        fetch('/web/apps/list').then(res => res.json()).then(data => setApps(data));
    };

    useEffect(() => { loadApps(); }, []);

    const toggleApp = async (technicalName, install) => {
        setLoading(true);
        await fetch('/web/apps/toggle', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ technicalName, install })
        });
        setLoading(false);
        loadApps();
        if (onAppToggled) onAppToggled();
    };

    return React.createElement('div', { className: 'container-fluid px-4 py-3' },
        React.createElement('div', { className: 'd-flex justify-content-between align-items-center mb-4' },
            React.createElement('h4', { className: 'fw-bold text-secondary m-0' }, '📦 Modular Apps Center'),
            loading ? React.createElement('div', { className: 'spinner-border spinner-border-sm text-primary' }) : null
        ),
        React.createElement('div', { className: 'row g-3' },
            apps.map(app => {
                const isInstalled = app.state === 'installed';
                return React.createElement('div', { key: app.technicalName, className: 'col-md-6 col-lg-3' },
                    React.createElement('div', { className: 'app-card d-flex flex-column justify-content-between h-100' },
                        React.createElement('div', null,
                            React.createElement('div', { className: 'd-flex justify-content-between align-items-start mb-2' },
                                React.createElement('h5', { className: 'fw-bold m-0 text-dark' }, app.name),
                                React.createElement('span', { className: `badge ${isInstalled ? 'bg-success' : 'bg-secondary'}` },
                                    isInstalled ? 'Installed' : 'Not Installed'
                                )
                            ),
                            React.createElement('div', { className: 'small text-muted mb-2' }, `Category: ${app.category}`),
                            React.createElement('p', { className: 'small text-secondary' }, app.summary || 'No description.')
                        ),
                        React.createElement('div', { className: 'd-flex justify-content-between align-items-center mt-3 pt-3 border-top' },
                            React.createElement('code', { className: 'small' }, app.technicalName),
                            isInstalled ? (
                                React.createElement('button', {
                                    type: 'button',
                                    className: 'btn btn-sm btn-outline-danger',
                                    onClick: () => toggleApp(app.technicalName, false)
                                }, React.createElement('i', { className: 'bi bi-x-circle me-1' }), 'Deactivate')
                            ) : (
                                React.createElement('button', {
                                    type: 'button',
                                    className: 'btn btn-sm btn-primary',
                                    onClick: () => toggleApp(app.technicalName, true)
                                }, React.createElement('i', { className: 'bi bi-download me-1' }), 'Activate')
                            )
                        )
                    )
                );
            })
        )
    );
}

function Chatter({ model, recordId }) {
    const [messages, setMessages] = useState([]);
    const [note, setNote] = useState('');

    const loadMessages = () => {
        if (!recordId) return;
        fetch(`/web/mail/chatter/${model}/${recordId}`).then(res => res.json()).then(data => setMessages(data));
    };

    useEffect(() => { loadMessages(); }, [model, recordId]);

    const postNote = async () => {
        if (!note.trim()) return;
        await fetch('/web/mail/post', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ model, id: recordId, body: note })
        });
        setNote('');
        loadMessages();
    };

    if (!recordId) return null;

    return React.createElement('div', { className: 'chatter-box shadow-sm' },
        React.createElement('h6', { className: 'fw-bold text-secondary mb-3' }, '💬 Chatter & Audit Trail'),
        React.createElement('div', { className: 'input-group mb-3' },
            React.createElement('input', {
                type: 'text',
                className: 'form-control form-control-sm',
                placeholder: 'Log an internal note...',
                value: note,
                onChange: (e) => setNote(e.target.value)
            }),
            React.createElement('button', { className: 'btn btn-sm btn-outline-secondary', onClick: postNote }, 'Send')
        ),
        React.createElement('div', { className: 'list-group list-group-flush' },
            messages.map(m => React.createElement('div', { key: m.id, className: 'list-group-item px-0 py-2 border-0' },
                React.createElement('div', { className: 'd-flex justify-content-between align-items-center' },
                    React.createElement('span', { className: 'fw-bold small text-primary' }, m.author),
                    React.createElement('span', { className: 'text-muted', style: { fontSize: '11px' } }, m.date)
                ),
                React.createElement('div', { className: `small ${m.type === 'notification' ? 'text-muted fst-italic' : 'text-dark'}` }, m.body)
            ))
        )
    );
}

function One2manyGrid({ fieldDef, lines = [], onLinesChange }) {
    const childModel = fieldDef.relation;
    const [childFields, setChildFields] = useState({});

    useEffect(() => {
        callKw(childModel, 'get_view', [], { view_type: 'tree' }).then(res => setChildFields(res.fields));
    }, [childModel]);

    const addLine = () => {
        const newLine = { id: `new_${Date.now()}` };
        for (const [fname, fdef] of Object.entries(childFields)) {
            if (fdef.defaultValue !== null) newLine[fname] = fdef.defaultValue;
        }
        onLinesChange([...lines, newLine]);
    };

    const updateCell = async (index, fieldName, value) => {
        const updated = [...lines];
        updated[index] = { ...updated[index], [fieldName]: value };

        if (childModel === 'account.move.line' && fieldName === 'lot_id') {
            try {
                const res = await callKw(childModel, 'onchange', [fieldName, updated[index]]);
                if (res && res.value) {
                    updated[index] = { ...updated[index], ...res.value };
                }
            } catch (err) { console.error(err); }
        }

        if (fieldName === 'quantity' || fieldName === 'price_unit' || fieldName === 'product_uom_qty') {
            const qty = parseFloat(updated[index].quantity || updated[index].product_uom_qty) || 0;
            const price = parseFloat(updated[index].price_unit) || 0;
            updated[index].price_subtotal = qty * price;
        }
        onLinesChange(updated);
    };

    const removeLine = (index) => {
        onLinesChange(lines.filter((_, i) => i !== index));
    };

    const cols = Object.keys(childFields).filter(k => k !== 'id' && k !== fieldDef.inverseName);

    return React.createElement('div', { className: 'col-12 mt-3' },
        React.createElement('label', { className: 'form-label fw-bold text-secondary small' }, fieldDef.string),
        React.createElement('div', { className: 'table-responsive border rounded-2' },
            React.createElement('table', { className: 'table table-sm table-bordered align-middle mb-0' },
                React.createElement('thead', { className: 'table-light' },
                    React.createElement('tr', null,
                        cols.map(c => React.createElement('th', { key: c, className: 'small text-secondary' }, childFields[c]?.string || c)),
                        React.createElement('th', { style: { width: '40px' } })
                    )
                ),
                React.createElement('tbody', null,
                    lines.map((line, idx) => React.createElement('tr', { key: line.id || idx },
                        cols.map(col => {
                            const fdef = childFields[col];
                            if (fdef && fdef.type === 5 && fdef.relation) {
                                return React.createElement('td', { key: col, className: 'p-1' },
                                    React.createElement('input', {
                                        className: 'form-control form-control-sm border-0 bg-transparent',
                                        value: line[col] ?? '',
                                        placeholder: 'Lot ID #',
                                        onChange: (e) => updateCell(idx, col, parseInt(e.target.value) || 0),
                                        type: 'number'
                                    })
                                );
                            }
                            return React.createElement('td', { key: col, className: 'p-1' },
                                React.createElement('input', {
                                    className: 'form-control form-control-sm border-0 bg-transparent',
                                    value: line[col] ?? '',
                                    onChange: (e) => updateCell(idx, col, fdef?.type === 1 || fdef?.type === 2 ? parseFloat(e.target.value) || 0 : e.target.value),
                                    type: fdef?.type === 1 || fdef?.type === 2 ? 'number' : 'text'
                                })
                            );
                        }),
                        React.createElement('td', { className: 'text-center p-1' },
                            React.createElement('button', {
                                type: 'button',
                                className: 'btn btn-sm btn-link text-danger p-0',
                                onClick: () => removeLine(idx)
                            }, React.createElement('i', { className: 'bi bi-trash' }))
                        )
                    ))
                )
            )
        ),
        React.createElement('button', {
            type: 'button',
            className: 'btn btn-sm btn-link text-decoration-none mt-1 p-0 fw-semibold',
            onClick: addLine
        }, React.createElement('i', { className: 'bi bi-plus-circle me-1' }), 'Add a line')
    );
}

function DynamicOdooForm({ model, recordId, onBack, onNavigateRelational }) {
    const [archDoc, setArchDoc] = useState(null);
    const [fields, setFields] = useState({});
    const [record, setRecord] = useState({});
    const [isNew, setIsNew] = useState(!recordId);
    const [relOptions, setRelOptions] = useState({});
    const [errorMsg, setErrorMsg] = useState(null);

    const loadData = async () => {
        const viewData = await callKw(model, 'get_view', [], { view_type: 'form' });
        const parser = new DOMParser();
        setArchDoc(parser.parseFromString(viewData.arch, 'text/xml'));
        setFields(viewData.fields);

        for (const [fname, fdef] of Object.entries(viewData.fields)) {
            if (fdef.type === 5 && fdef.relation) {
                const options = await callKw(fdef.relation, 'name_search', []);
                setRelOptions(prev => ({ ...prev, [fname]: options }));
            }
        }

        if (recordId) {
            const data = await callKw(model, 'search_read', [], { fields: Object.keys(viewData.fields) });
            const rec = data.find(r => r.id === recordId) || {};
            setRecord(rec);
        }
    };

    useEffect(() => { loadData(); }, [model, recordId]);

    const handleFieldChange = async (name, value) => {
        setErrorMsg(null);
        const updated = { ...record, [name]: value };
        setRecord(updated);

        try {
            const res = await callKw(model, 'onchange', [name, updated]);
            if (res && res.value) setRecord(prev => ({ ...prev, ...res.value }));
        } catch (e) { setErrorMsg(e.message); }
    };

    const handleLinesChange = (one2manyFieldName, newLines) => {
        const updated = { ...record, [one2manyFieldName]: newLines };
        const total = newLines.reduce((sum, l) => sum + (parseFloat(l.price_subtotal) || 0), 0);
        if (fields['amount_total']) updated['amount_total'] = total;
        if (fields['amount_untaxed']) updated['amount_untaxed'] = total;
        setRecord(updated);
    };

    const handleSave = async () => {
        setErrorMsg(null);
        try {
            let masterId = recordId;
            if (isNew) masterId = await callKw(model, 'create', [record]);
            else await callKw(model, 'write', [recordId, record]);

            for (const [fname, fdef] of Object.entries(fields)) {
                if (fdef.type === 6 && record[fname]) {
                    for (const line of record[fname]) {
                        const childPayload = { ...line, [fdef.inverseName]: masterId };
                        if (String(line.id).startsWith('new_')) {
                            delete childPayload.id;
                            await callKw(fdef.relation, 'create', [childPayload]);
                        } else {
                            await callKw(fdef.relation, 'write', [line.id, childPayload]);
                        }
                    }
                }
            }
            onBack();
        } catch (e) { setErrorMsg(e.message); }
    };

    const handleButtonClick = async (btnNode) => {
        setErrorMsg(null);
        try {
            const btnType = btnNode.getAttribute('type');
            const btnName = btnNode.getAttribute('name');
            if (btnType === 'object') {
                await callKw(model, btnName, [recordId || 0, record]);
                await loadData();
            }
        } catch (e) { setErrorMsg(e.message); }
    };

    if (!archDoc) return React.createElement('div', { className: 'p-5 text-center' }, React.createElement('div', { className: 'spinner-border text-primary' }));

    const renderElements = (node) => {
        return Array.from(node.children).map((child, i) => {
            if (child.tagName === 'header') {
                return React.createElement('div', { key: i, className: 'o-statusbar d-flex justify-content-between align-items-center mb-3' },
                    React.createElement('div', { className: 'btn-group' }, renderElements(child)),
                    record.status ? React.createElement('span', { className: 'badge bg-secondary fs-6' }, String(record.status)) : null
                );
            }
            if (child.tagName === 'button') {
                const isInvisible = evalModifier(child.getAttribute('invisible'), record);
                if (isInvisible) return null;

                return React.createElement('button', {
                    key: i,
                    type: 'button',
                    className: `btn btn-sm ${child.getAttribute('class') || 'btn-outline-primary'} me-2`,
                    onClick: () => handleButtonClick(child)
                }, child.getAttribute('string'));
            }
            if (child.tagName === 'div' && child.getAttribute('class') === 'oe_button_box') {
                return React.createElement('div', { key: i, className: 'oe_button_box' },
                    Array.from(child.children).map((btn, bIdx) => {
                        return React.createElement('div', {
                            key: bIdx,
                            className: 'oe_stat_button shadow-sm',
                            onClick: () => onNavigateRelational(btn.getAttribute('count_model'), btn.getAttribute('count_field'), recordId)
                        },
                            React.createElement('i', { className: `bi ${btn.getAttribute('icon')} text-primary fs-5` }),
                            React.createElement('div', null,
                                React.createElement('div', { className: 'text-muted', style: { fontSize: '10px' } }, btn.getAttribute('label')),
                                React.createElement('div', { className: 'fw-bold text-dark' }, 'View')
                            )
                        );
                    })
                );
            }
            if (child.tagName === 'sheet') {
                return React.createElement('div', { key: i, className: 'o-form-sheet p-4 mx-auto my-3' }, renderElements(child));
            }
            if (child.tagName === 'group') {
                const groupTitle = child.getAttribute('string');
                return React.createElement('div', { key: i, className: 'card mb-3 border-0 bg-transparent' },
                    groupTitle ? React.createElement('h6', { className: 'text-muted border-bottom pb-2 mb-3' }, groupTitle) : null,
                    React.createElement('div', { className: 'row g-3' }, renderElements(child))
                );
            }
            if (child.tagName === 'field') {
                const fieldName = child.getAttribute('name');
                const fieldDef = fields[fieldName] || { string: fieldName, type: 0 };

                const isInvisible = evalModifier(child.getAttribute('invisible'), record);
                if (isInvisible) return null;

                // Explicitly respect explicit readonly attribute only when explicitly true, avoiding blank-state coercion locking
                const readonlyAttr = child.getAttribute('readonly');
                const isReadonly = readonlyAttr === '1' || readonlyAttr === 'True' || fieldDef.readonly || (readonlyAttr ? evalModifier(readonlyAttr, record) : false);
                const isRequired = child.getAttribute('required'] === '1' || fieldDef.required || evalModifier(child.getAttribute('required'), record);

                if (fieldDef.type === 6) {
                    return React.createElement(One2manyGrid, {
                        key: fieldName,
                        fieldDef: fieldDef,
                        lines: record[fieldName] || [],
                        onLinesChange: (lines) => handleLinesChange(fieldName, lines)
                    });
                }

                if (fieldDef.type === 3) {
                    return React.createElement('div', { key: fieldName, className: 'col-md-6 d-flex align-items-center mt-4' },
                        React.createElement('div', { className: 'form-check' },
                            React.createElement('input', {
                                className: 'form-check-input',
                                type: 'checkbox',
                                id: fieldName,
                                checked: Boolean(record[fieldName]),
                                disabled: isReadonly,
                                onChange: (e) => handleFieldChange(fieldName, e.target.checked)
                            }),
                            React.createElement('label', { className: 'form-check-label fw-semibold text-secondary small ms-1', htmlFor: fieldName }, fieldDef.string)
                        )
                    );
                }

                if (fieldDef.type === 5) {
                    const currentVal = Array.isArray(record[fieldName]) ? record[fieldName][0] : record[fieldName];
                    return React.createElement('div', { key: fieldName, className: 'col-md-6' },
                        React.createElement('label', { className: 'form-label fw-semibold text-secondary small' },
                            fieldDef.string, isRequired ? React.createElement('span', { className: 'text-danger' }, ' *') : null
                        ),
                        React.createElement('select', {
                            className: `form-select form-select-sm ${isRequired && !currentVal ? 'border-danger' : ''}`,
                            value: currentVal || '',
                            disabled: isReadonly,
                            onChange: (e) => handleFieldChange(fieldName, parseInt(e.target.value))
                        },
                            React.createElement('option', { value: '' }, '-- Select --'),
                            (relOptions[fieldName] || []).map(([optId, optName]) =>
                                React.createElement('option', { key: optId, value: optId }, optName)
                            )
                        )
                    );
                }

                if (fieldDef.type === 4 && fieldDef.selection) {
                    return React.createElement('div', { key: fieldName, className: 'col-md-6' },
                        React.createElement('label', { className: 'form-label fw-semibold text-secondary small' }, fieldDef.string),
                        React.createElement('select', {
                            className: 'form-select form-select-sm',
                            value: record[fieldName] || '',
                            disabled: isReadonly,
                            onChange: (e) => handleFieldChange(fieldName, e.target.value)
                        },
                            fieldDef.selection.map(s => React.createElement('option', { key: s.value, value: s.value }, s.label))
                        )
                    );
                }

                return React.createElement('div', { key: fieldName, className: 'col-md-6' },
                    React.createElement('label', { className: 'form-label fw-semibold text-secondary small' },
                        fieldDef.string, isRequired ? React.createElement('span', { className: 'text-danger' }, ' *') : null
                    ),
                    React.createElement('input', {
                        className: `form-control form-control-sm ${isRequired && !record[fieldName] ? 'border-danger' : ''}`,
                        value: Array.isArray(record[fieldName]) ? record[fieldName][1] : (record[fieldName] ?? ''),
                        readOnly: isReadonly,
                        onChange: (e) => handleFieldChange(fieldName, fieldDef.type === 1 || fieldDef.type === 2 ? parseFloat(e.target.value) || 0 : e.target.value),
                        type: fieldDef.type === 1 || fieldDef.type === 2 ? 'number' : 'text'
                    })
                );
            }
            return null;
        });
    };

    return React.createElement('div', null,
        React.createElement('div', { className: 'd-flex justify-content-between align-items-center bg-white p-3 border-bottom shadow-sm mb-4' },
            React.createElement('div', { className: 'btn-group' },
                React.createElement('button', { className: 'btn btn-sm o-btn-primary', onClick: handleSave },
                    React.createElement('i', { className: 'bi bi-check-lg me-1' }), 'Save'
                ),
                React.createElement('button', { className: 'btn btn-sm btn-outline-secondary', onClick: onBack },
                    React.createElement('i', { className: 'bi bi-x-lg me-1' }), 'Discard'
                )
            ),
            React.createElement('span', { className: 'badge text-bg-light border text-secondary' }, isNew ? 'New' : `ID: #${recordId}`)
        ),
        errorMsg ? React.createElement('div', { className: 'container mb-3' },
            React.createElement('div', { className: 'alert alert-danger shadow-sm' },
                React.createElement('i', { className: 'bi bi-exclamation-triangle-fill me-2' }),
                React.createElement('strong', null, 'Validation Error: '), errorMsg
            )
        ) : null,
        React.createElement('div', { className: 'container' }, renderElements(archDoc.documentElement)),
        React.createElement(Chatter, { model: model, recordId: recordId })
    );
}

function DynamicOdooKanban({ model, onOpenRecord, domain }) {
    const [records, setRecords] = useState([]);
    const [fields, setFields] = useState({});
    const [kanbanFields, setKanbanFields] = useState([]);

    useEffect(() => {
        async function load() {
            const viewData = await callKw(model, 'get_view', [], { view_type: 'kanban' });
            const parser = new DOMParser();
            const xml = parser.parseFromString(viewData.arch, 'text/xml');
            const fieldNodes = Array.from(xml.querySelectorAll('field')).map(n => n.getAttribute('name'));

            setKanbanFields(fieldNodes);
            setFields(viewData.fields);

            const data = await callKw(model, 'search_read', [], { fields: fieldNodes, domain: domain });
            setRecords(data);
        }
        load();
    }, [model, domain]);

    return React.createElement('div', { className: 'container-fluid px-4 py-3' },
        React.createElement('div', { className: 'row g-3' },
            records.map(rec => React.createElement('div', { key: rec.id, className: 'col-md-4 col-lg-3' },
                React.createElement('div', { 
                    className: 'card kanban-card shadow-sm border-0 h-100 p-3 bg-white',
                    onClick: () => onOpenRecord(rec.id)
                },
                    React.createElement('div', { className: 'fw-bold fs-6 text-primary mb-2' }, 
                        rec[kanbanFields[0]] ? (Array.isArray(rec[kanbanFields[0]]) ? rec[kanbanFields[0]][1] : rec[kanbanFields[0]]) : `#${rec.id}`
                    ),
                    kanbanFields.slice(1).map(f => React.createElement('div', { key: f, className: 'small text-muted mb-1' },
                        React.createElement('span', { className: 'fw-semibold' }, `${fields[f]?.string || f}: `),
                        Array.isArray(rec[f]) ? rec[f][1] : String(rec[f] ?? '')
                    ))
                )
            ))
        )
    );
}

function DynamicOdooList({ model, onOpenRecord, domain }) {
    const [records, setRecords] = useState([]);
    const [treeFields, setTreeFields] = useState([]);
    const [fieldDefs, setFieldDefs] = useState({});

    useEffect(() => {
        async function load() {
            const viewData = await callKw(model, 'get_view', [], { view_type: 'tree' });
            const parser = new DOMParser();
            const xml = parser.parseFromString(viewData.arch, 'text/xml');
            const fieldNodes = Array.from(xml.querySelectorAll('field')).map(n => n.getAttribute('name'));

            setTreeFields(fieldNodes);
            setFieldDefs(viewData.fields);

            const data = await callKw(model, 'search_read', [], { fields: fieldNodes, domain: domain });
            setRecords(data);
        }
        load();
    }, [model, domain]);

    const formatCell = (val) => Array.isArray(val) ? val[1] : (typeof val === 'boolean' ? (val ? '✔' : '✖') : String(val ?? ''));

    return React.createElement('div', { className: 'container-fluid px-4' },
        React.createElement('div', { className: 'card shadow-sm border-0' },
            React.createElement('div', { className: 'table-responsive' },
                React.createElement('table', { className: 'table table-hover align-middle mb-0' },
                    React.createElement('thead', { className: 'table-light' },
                        React.createElement('tr', null,
                            treeFields.map(f => React.createElement('th', { key: f, className: 'small text-secondary fw-semibold' }, fieldDefs[f]?.string || f))
                        )
                    ),
                    React.createElement('tbody', null,
                        records.map(rec => React.createElement('tr', { 
                            key: rec.id, 
                            onClick: () => onOpenRecord(rec.id),
                            style: { cursor: 'pointer' }
                        },
                            treeFields.map(f => React.createElement('td', { key: f, className: 'small' }, formatCell(rec[f])))
                        ))
                    )
                )
            )
        )
    );
}

function WebClient() {
    const [menus, setMenus] = useState([]);
    const [activeMenu, setActiveMenu] = useState(null);
    const [viewMode, setViewMode] = useState('tree');
    const [selectedRecordId, setSelectedRecordId] = useState(null);
    const [domain, setDomain] = useState([]);

    const loadSessionMenus = () => {
        fetch('/web/session/modules').then(res => res.json()).then(data => {
            setMenus(data);
            if (!activeMenu || !data.some(m => m.id === activeMenu.id)) {
                if (data.length > 0) setActiveMenu(data[0]);
            }
        });
    };

    useEffect(() => { loadSessionMenus(); }, []);

    const handleNavigateRelational = (targetModel, field, id) => {
        const targetMenu = menus.find(m => m.targetModel === targetModel);
        if (targetMenu) {
            setActiveMenu(targetMenu);
            setDomain([[field, '=', id]]);
            setViewMode('tree');
        }
    };

    if (!activeMenu) return React.createElement('div', { className: 'p-5 text-center' }, 'Loading Suite...');

    return React.createElement(React.Fragment, null,
        React.createElement('nav', { className: 'navbar navbar-expand o-navbar navbar-dark shadow-sm px-3' },
            React.createElement('a', { className: 'navbar-brand fw-bold d-flex align-items-center gap-2 me-4', href: '#' },
                React.createElement('i', { className: 'bi bi-shop' }),
                'Odoo Enterprise ERP'
            ),
            React.createElement('div', { className: 'navbar-nav me-auto' },
                menus.map(m => React.createElement('a', {
                    key: m.id,
                    className: `nav-link px-3 ${activeMenu.id === m.id ? 'active fw-bold' : 'text-white-50'}`,
                    onClick: () => { setActiveMenu(m); setViewMode('tree'); setSelectedRecordId(null); setDomain([]); }
                },
                    React.createElement('i', { className: `bi ${m.icon} me-1` }),
                    m.name
                ))
            )
        ),
        activeMenu.actionType !== 'client_action' && viewMode !== 'form' ? (
            React.createElement('div', { className: 'd-flex justify-content-between align-items-center bg-white p-3 border-bottom shadow-sm mb-3' },
                React.createElement('button', { 
                    className: 'btn btn-sm o-btn-primary', 
                    onClick: () => { setSelectedRecordId(null); setViewMode('form'); } 
                }, React.createElement('i', { className: 'bi bi-plus-lg me-1' }), 'New'),
                React.createElement('div', { className: 'btn-group' },
                    React.createElement('button', {
                        className: `btn btn-sm ${viewMode === 'tree' ? 'btn-secondary' : 'btn-outline-secondary'}`,
                        onClick: () => setViewMode('tree')
                    }, React.createElement('i', { className: 'bi bi-list-ul' })),
                    React.createElement('button', {
                        className: `btn btn-sm ${viewMode === 'kanban' ? 'btn-secondary' : 'btn-outline-secondary'}`,
                        onClick: () => setViewMode('kanban')
                    }, React.createElement('i', { className: 'bi bi-kanban' }))
                )
            )
        ) : null,
        React.createElement('div', { className: 'main-container' },
            activeMenu.actionType === 'client_action'
                ? React.createElement(DynamicModulePage, { scriptUrl: activeMenu.clientComponentUrl, componentName: activeMenu.clientComponentExport })
                : viewMode === 'form'
                    ? React.createElement(DynamicOdooForm, {
                        model: activeMenu.targetModel,
                        recordId: selectedRecordId,
                        onBack: () => setViewMode('tree'),
                        onNavigateRelational: handleNavigateRelational
                    })
                    : viewMode === 'kanban'
                        ? React.createElement(DynamicOdooKanban, {
                            model: activeMenu.targetModel,
                            domain: domain,
                            onOpenRecord: (id) => { setSelectedRecordId(id); setViewMode('form'); }
                        })
                        : React.createElement(DynamicOdooList, {
                            model: activeMenu.targetModel,
                            domain: domain,
                            onOpenRecord: (id) => { setSelectedRecordId(id); setViewMode('form'); }
                        })
        )
    );
}

function DynamicModulePage({ scriptUrl, componentName }) {
    const [Component, setComponent] = useState(null);
    const [error, setError] = useState(null);
    useEffect(() => {
        import(scriptUrl)
            .then(mod => setComponent(() => mod[componentName] || mod.default))
            .catch(err => setError(err.message));
    }, [scriptUrl, componentName]);

    if (error) return React.createElement('div', { className: 'alert alert-danger m-4' }, `Failed to load report: {error}`);
    if (!Component) return React.createElement('div', { className: 'p-5 text-center' }, React.createElement('div', { className: 'spinner-border text-primary' }));
    return React.createElement(Component, { callKw });
}

createRoot(document.getElementById('root')).render(React.createElement(WebClient));
