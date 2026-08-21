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
    const [search, setSearch] = useState('');
    const [appFilter, setAppFilter] = useState('all');
    const [stateFilter, setStateFilter] = useState('all');
    const [error, setError] = useState(null);

    const loadApps = () => {
        fetch('/web/apps/list').then(res => res.json()).then(data => setApps(data));
    };

    useEffect(() => { loadApps(); }, []);

    const toggleApp = async (technicalName, install) => {
        setLoading(true);
        setError(null);
        const res = await fetch('/web/apps/toggle', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ technicalName, install })
        });
        if (!res.ok) {
            const data = await res.json().catch(() => ({}));
            setError(data.error || 'Failed to update module.');
        }
        setLoading(false);
        loadApps();
        if (onAppToggled) onAppToggled();
    };

    const q = search.trim().toLowerCase();
    const filtered = apps.filter(app => {
        if (appFilter === 'apps' && !app.application) return false;
        if (appFilter === 'extra' && app.application) return false;
        if (stateFilter === 'installed' && app.state !== 'installed') return false;
        if (stateFilter === 'not_installed' && app.state === 'installed') return false;
        if (q && !app.name.toLowerCase().includes(q) && !(app.summary || '').toLowerCase().includes(q) && !app.technicalName.toLowerCase().includes(q)) return false;
        return true;
    });

    const grouped = {};
    for (const app of filtered) (grouped[app.category || 'Uncategorized'] ??= []).push(app);
    const categories = Object.keys(grouped).sort();

    const filterBtn = (value, current, setter, label) => React.createElement('button', {
        type: 'button',
        className: `btn btn-sm ${current === value ? 'btn-secondary' : 'btn-outline-secondary'} me-1`,
        onClick: () => setter(value)
    }, label);

    return React.createElement('div', { className: 'container-fluid px-4 py-3' },
        React.createElement('div', { className: 'd-flex justify-content-between align-items-center mb-3' },
            React.createElement('h4', { className: 'fw-bold text-secondary m-0' }, '📦 Modular Apps Center'),
            loading ? React.createElement('div', { className: 'spinner-border spinner-border-sm text-primary' }) : null
        ),
        error ? React.createElement('div', { className: 'alert alert-danger py-2' }, error) : null,
        React.createElement('div', { className: 'd-flex flex-wrap align-items-center gap-2 mb-4' },
            React.createElement('input', {
                className: 'form-control form-control-sm', style: { maxWidth: 260 },
                placeholder: 'Search apps...', value: search, onChange: e => setSearch(e.target.value)
            }),
            React.createElement('div', null, filterBtn('all', appFilter, setAppFilter, 'All'), filterBtn('apps', appFilter, setAppFilter, 'Apps'), filterBtn('extra', appFilter, setAppFilter, 'Extra')),
            React.createElement('div', null, filterBtn('all', stateFilter, setStateFilter, 'Any Status'), filterBtn('installed', stateFilter, setStateFilter, 'Installed'), filterBtn('not_installed', stateFilter, setStateFilter, 'Not Installed'))
        ),
        categories.map(cat => React.createElement('div', { key: cat, className: 'mb-4' },
            React.createElement('h6', { className: 'text-muted border-bottom pb-2 mb-3' }, `${cat} (${grouped[cat].length})`),
            React.createElement('div', { className: 'row g-3' },
                grouped[cat].map(app => {
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
                                React.createElement('div', { className: 'small text-muted' }, `v${app.version} · By ${app.author}`),
                                app.website ? React.createElement('a', { className: 'small', href: app.website, target: '_blank', rel: 'noreferrer' }, app.website) : null,
                                React.createElement('p', { className: 'small text-secondary mt-2 mb-1' }, app.summary || 'No description.'),
                                app.depends && app.depends.length > 0 ? React.createElement('div', { className: 'small text-muted' },
                                    'Depends on: ', app.depends.join(', ')
                                ) : null
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
        ))
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
    const [relOptions, setRelOptions] = useState({});

    useEffect(() => {
        async function loadChildView() {
            const res = await callKw(childModel, 'get_view', [], { view_type: 'tree' });
            setChildFields(res.fields);

            for (const [fname, fdef] of Object.entries(res.fields)) {
                if (fdef.type === 5 && fdef.relation) {
                    const opts = await callKw(fdef.relation, 'name_search', []);
                    setRelOptions(prev => ({ ...prev, [fname]: opts }));
                }
            }
        }
        loadChildView();
    }, [childModel]);

    const addLine = () => {
        const newLine = { id: `new_${Date.now()}` };
        for (const [fname, fdef] of Object.entries(childFields)) {
            if (fdef.defaultValue !== null) newLine[fname] = fdef.defaultValue;
        }
        onLinesChange([...lines, newLine]);
    };

    const updateCells = async (index, patch) => {
        const updated = [...lines];
        updated[index] = { ...updated[index], ...patch };

        try {
            const res = await callKw(childModel, 'onchange', [Object.keys(patch)[0], updated[index]]);
            if (res && res.value) {
                updated[index] = { ...updated[index], ...res.value };
            }
        } catch (err) { }

        if ('quantity' in patch || 'price_unit' in patch || 'product_uom_qty' in patch) {
            const qty = parseFloat(updated[index].quantity || updated[index].product_uom_qty) || 0;
            const price = parseFloat(updated[index].price_unit) || 0;
            updated[index].price_subtotal = qty * price;
        }
        onLinesChange(updated);
    };

    const updateCell = (index, fieldName, value) => updateCells(index, { [fieldName]: value });

    const quickCreateRelated = async (idx, col, relation, label) => {
        const name = window.prompt(`New ${label}:`);
        if (!name) return;
        const payload = { name };
        const isProduct = relation === 'product.template';
        const price = isProduct ? parseFloat(window.prompt('Price:', '0')) || 0 : null;
        if (isProduct) { payload.list_price = price; payload.standard_price = price; }

        const newId = await callKw(relation, 'create', [payload]);
        setRelOptions(prev => ({ ...prev, [col]: [...(prev[col] || []), [newId, name]] }));
        await updateCells(idx, isProduct && childFields['price_unit'] ? { [col]: newId, price_unit: price } : { [col]: newId });
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
                                const currentVal = Array.isArray(line[col]) ? line[col][0] : line[col];
                                return React.createElement('td', { key: col, className: 'p-1' },
                                    React.createElement('select', {
                                        className: 'form-select form-select-sm border-0 bg-transparent',
                                        value: currentVal || '',
                                        onChange: (e) => {
                                            if (e.target.value === '__new__') {
                                                quickCreateRelated(idx, col, fdef.relation, fdef.string || col);
                                                return;
                                            }
                                            updateCell(idx, col, parseInt(e.target.value) || 0);
                                        }
                                    },
                                        React.createElement('option', { value: '' }, '-- Select --'),
                                        (relOptions[col] || []).map(([optId, optName]) =>
                                            React.createElement('option', { key: optId, value: optId }, optName)
                                        ),
                                        React.createElement('option', { value: '__new__' }, `+ Add new ${fdef.string || col}...`)
                                    )
                                );
                            }
                            return React.createElement('td', { key: col, className: 'p-1' },
                                React.createElement('input', {
                                    className: 'form-control form-control-sm border-0 bg-transparent',
                                    value: line[col] ?? '',
                                    readOnly: fdef?.readonly,
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

function DynamicOdooForm({ model, recordId, onBack, onNavigateRelational, onDuplicated }) {
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
            if ((fdef.type === 5 || fdef.type === 9) && fdef.relation) {
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
            } else if (btnType === 'report') {
                if (!recordId) { setErrorMsg('Save the record before printing.'); return; }
                const template = btnNode.getAttribute('template');
                window.open(`/web/report/${encodeURIComponent(model)}/${encodeURIComponent(template)}/${recordId}`, '_blank');
            }
        } catch (e) { setErrorMsg(e.message); }
    };

    const handleDuplicate = async () => {
        setErrorMsg(null);
        try {
            const newId = await callKw(model, 'copy', [recordId]);
            onDuplicated(newId);
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

                const readonlyAttr = child.getAttribute('readonly');
                const isReadonly = readonlyAttr === '1' || readonlyAttr === 'True' || fieldDef.readonly || (readonlyAttr ? evalModifier(readonlyAttr, record) : false);
                const isRequired = child.getAttribute('required') === '1' || fieldDef.required || evalModifier(child.getAttribute('required'), record);

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

                if (fieldDef.type === 9) {
                    const currentIds = (record[fieldName] || []).map(t => String(Array.isArray(t) ? t[0] : t));
                    const options = relOptions[fieldName] || [];
                    return React.createElement('div', { key: fieldName, className: 'col-md-6' },
                        React.createElement('label', { className: 'form-label fw-semibold text-secondary small' }, fieldDef.string),
                        React.createElement('select', {
                            multiple: true,
                            className: 'form-select form-select-sm',
                            value: currentIds,
                            disabled: isReadonly,
                            size: Math.min(6, Math.max(3, options.length)),
                            onChange: (e) => handleFieldChange(fieldName, Array.from(e.target.selectedOptions).map(o => parseInt(o.value)))
                        },
                            options.map(([optId, optName]) =>
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
                ),
                !isNew ? React.createElement('button', { className: 'btn btn-sm btn-outline-secondary', onClick: handleDuplicate },
                    React.createElement('i', { className: 'bi bi-copy me-1' }), 'Duplicate'
                ) : null
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

function LoginScreen({ onLogin }) {
    const [login, setLogin] = useState('admin');
    const [password, setPassword] = useState('');
    const [error, setError] = useState(null);
    const [busy, setBusy] = useState(false);

    const submit = async (e) => {
        e.preventDefault();
        setBusy(true);
        setError(null);
        try {
            const res = await fetch('/web/session/login', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ login, password })
            });
            const data = await res.json();
            if (!res.ok) throw new Error(data.error || 'Login failed');
            onLogin(data);
        } catch (err) {
            setError(err.message);
        } finally {
            setBusy(false);
        }
    };

    return React.createElement('div', { className: 'd-flex align-items-center justify-content-center', style: { height: '100vh' } },
        React.createElement('form', { onSubmit: submit, className: 'card p-4 shadow-sm', style: { width: 340 } },
            React.createElement('h4', { className: 'mb-3 text-center' }, 'Odoo Enterprise ERP'),
            error ? React.createElement('div', { className: 'alert alert-danger py-2' }, error) : null,
            React.createElement('div', { className: 'mb-2' },
                React.createElement('label', { className: 'form-label' }, 'Login'),
                React.createElement('input', { className: 'form-control', value: login, onChange: e => setLogin(e.target.value), autoFocus: true })
            ),
            React.createElement('div', { className: 'mb-3' },
                React.createElement('label', { className: 'form-label' }, 'Password'),
                React.createElement('input', { type: 'password', className: 'form-control', value: password, onChange: e => setPassword(e.target.value) })
            ),
            React.createElement('button', { type: 'submit', className: 'btn o-btn-primary w-100', disabled: busy }, busy ? 'Signing in...' : 'Log in')
        )
    );
}

function AppRoot() {
    const [user, setUser] = useState(undefined);

    useEffect(() => {
        fetch('/web/session/whoami').then(res => res.ok ? res.json() : null).then(setUser);
    }, []);

    const logout = async () => {
        await fetch('/web/session/logout', { method: 'POST' });
        setUser(null);
    };

    if (user === undefined) return React.createElement('div', { className: 'p-5 text-center' }, 'Loading Suite...');
    if (!user) return React.createElement(LoginScreen, { onLogin: setUser });
    return React.createElement(WebClient, { user, onLogout: logout });
}

function WebClient({ user, onLogout }) {
    const [menus, setMenus] = useState([]);
    const [activeMenu, setActiveMenu] = useState(null);
    const [viewMode, setViewMode] = useState('tree');
    const [selectedRecordId, setSelectedRecordId] = useState(null);
    const [domain, setDomain] = useState([]);

    const parseHash = () => {
        const match = window.location.hash.match(/^#\/app\/(\d+)(?:\/(tree|kanban|form))?(?:\/(new|\d+))?$/);
        if (!match) return null;
        return {
            menuId: parseInt(match[1]),
            viewMode: match[2] || 'tree',
            recordId: match[3] && match[3] !== 'new' ? parseInt(match[3]) : null
        };
    };

    const applyHash = (menuList) => {
        const parsed = parseHash();
        const menu = parsed && menuList.find(m => m.id === parsed.menuId);
        if (!menu) return false;
        setActiveMenu(menu);
        setViewMode(parsed.viewMode);
        setSelectedRecordId(parsed.recordId);
        return true;
    };

    const navigate = (menuId, mode = 'tree', recordId = null) => {
        let h = `#/app/${menuId}/${mode}`;
        if (mode === 'form') h += `/${recordId ?? 'new'}`;
        window.location.hash = h;
    };

    const loadSessionMenus = () => {
        fetch('/web/session/modules').then(res => res.json()).then(data => {
            setMenus(data);
            if (!applyHash(data) && data.length > 0) {
                setActiveMenu(data[0]);
                navigate(data[0].id, 'tree');
            }
        });
    };

    useEffect(() => { loadSessionMenus(); }, []);

    useEffect(() => {
        const onHashChange = () => applyHash(menus);
        window.addEventListener('hashchange', onHashChange);
        return () => window.removeEventListener('hashchange', onHashChange);
    }, [menus]);

    const selectMenu = (m) => {
        setDomain([]);
        navigate(m.id, 'tree');
    };

    const handleNavigateRelational = (targetModel, field, id) => {
        const targetMenu = menus.find(m => m.targetModel === targetModel);
        if (targetMenu) {
            setDomain([[field, '=', id]]);
            navigate(targetMenu.id, 'tree');
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
                    onClick: () => selectMenu(m)
                },
                    React.createElement('i', { className: `bi ${m.icon} me-1` }),
                    m.name
                ))
            ),
            React.createElement('div', { className: 'navbar-nav ms-auto' },
                React.createElement('span', { className: 'nav-link text-white-50' },
                    React.createElement('i', { className: 'bi bi-person-circle me-1' }), user.name),
                React.createElement('a', { className: 'nav-link text-white-50', onClick: onLogout, style: { cursor: 'pointer' } },
                    React.createElement('i', { className: 'bi bi-box-arrow-right me-1' }), 'Logout')
            )
        ),
        activeMenu.actionType !== 'client_action' && viewMode !== 'form' ? (
            React.createElement('div', { className: 'd-flex justify-content-between align-items-center bg-white p-3 border-bottom shadow-sm mb-3' },
                React.createElement('button', {
                    className: 'btn btn-sm o-btn-primary',
                    onClick: () => navigate(activeMenu.id, 'form')
                }, React.createElement('i', { className: 'bi bi-plus-lg me-1' }), 'New'),
                React.createElement('div', { className: 'btn-group' },
                    React.createElement('button', {
                        className: `btn btn-sm ${viewMode === 'tree' ? 'btn-secondary' : 'btn-outline-secondary'}`,
                        onClick: () => navigate(activeMenu.id, 'tree')
                    }, React.createElement('i', { className: 'bi bi-list-ul' })),
                    React.createElement('button', {
                        className: `btn btn-sm ${viewMode === 'kanban' ? 'btn-secondary' : 'btn-outline-secondary'}`,
                        onClick: () => navigate(activeMenu.id, 'kanban')
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
                        onBack: () => navigate(activeMenu.id, 'tree'),
                        onNavigateRelational: handleNavigateRelational,
                        onDuplicated: (newId) => navigate(activeMenu.id, 'form', newId)
                    })
                    : viewMode === 'kanban'
                        ? React.createElement(DynamicOdooKanban, {
                            model: activeMenu.targetModel,
                            domain: domain,
                            onOpenRecord: (id) => navigate(activeMenu.id, 'form', id)
                        })
                        : React.createElement(DynamicOdooList, {
                            model: activeMenu.targetModel,
                            domain: domain,
                            onOpenRecord: (id) => navigate(activeMenu.id, 'form', id)
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

createRoot(document.getElementById('root')).render(React.createElement(AppRoot));
