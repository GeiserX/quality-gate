var PLUGIN_ID = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890';
var config = { Policies: [], UserPolicies: [], DefaultPolicyId: '', DefaultIntroVideoPath: '' };
var users = [];
var isLoaded = false;

function escapeHtml(text) {
    var div = document.createElement('div');
    div.textContent = text || '';
    return div.innerHTML;
}

function escapeAttribute(text) {
    return String(text || '')
        .replace(/&/g, '&amp;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;');
}

function generateId() {
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
        var r = Math.random() * 16 | 0;
        return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
    });
}

function applyStatus(element, text, tone) {
    if (!element) {
        return;
    }

    element.textContent = text || '';
    element.classList.remove('qg-status-success', 'qg-status-warning', 'qg-status-error');
    if (tone) {
        element.classList.add('qg-status-' + tone);
    }
}

function setLoadStatus(view, text, tone) {
    applyStatus(view.querySelector('#loadingStatus'), text, tone);
}

function setSaveStatus(view, text, tone) {
    applyStatus(view.querySelector('#saveStatus'), text, tone);
}

function markDirty(view) {
    setSaveStatus(view, 'Unsaved changes.', 'warning');
}

function setStaticControlsEnabled(view, enabled) {
    var saveButton = view.querySelector('button[type="submit"]');
    var addPolicyButton = view.querySelector('#btnAddPolicy');
    var defaultPolicySelect = view.querySelector('#defaultPolicySelect');
    var defaultIntroPath = view.querySelector('#defaultIntroPath');

    if (saveButton) {
        saveButton.disabled = !enabled;
    }

    if (addPolicyButton) {
        addPolicyButton.disabled = !enabled;
    }

    if (defaultPolicySelect) {
        defaultPolicySelect.disabled = !enabled;
    }

    if (defaultIntroPath) {
        defaultIntroPath.disabled = !enabled;
    }
}

function resetViewState(view) {
    view.querySelector('#policiesContainer').innerHTML = '';
    view.querySelector('#userAccessContainer').innerHTML = '';
    view.querySelector('#defaultPolicySelect').innerHTML = '<option value="">(No default — Full Access)</option>';
    view.querySelector('#defaultIntroPath').value = '';
}

function getEnabledPolicy(policyId) {
    return config.Policies.find(function (policy) {
        return policy.Id === policyId && policy.Enabled;
    });
}

function hasInvalidDefaultPolicy() {
    return Boolean(config.DefaultPolicyId) && !getEnabledPolicy(config.DefaultPolicyId);
}

function getDefaultPolicyLabel() {
    if (!config.DefaultPolicyId) {
        return 'Full Access';
    }

    var policy = getEnabledPolicy(config.DefaultPolicyId);
    return policy ? policy.Name : 'Invalid default → Full Access';
}

function getUserOverride(userId) {
    return config.UserPolicies.find(function (up) { return up.UserId === userId; });
}

function isValidOverride(policyId) {
    if (!policyId || policyId === '__FULL_ACCESS__') {
        return true;
    }

    return Boolean(getEnabledPolicy(policyId));
}

function getEffectivePolicy(userId) {
    var override = getUserOverride(userId);

    if (override) {
        if (override.PolicyId === '__FULL_ACCESS__') {
            return { name: 'Full Access', restricted: false, denied: false, warning: false };
        }

        var overridePolicy = getEnabledPolicy(override.PolicyId);
        if (overridePolicy) {
            return { name: overridePolicy.Name, restricted: true, denied: false, warning: false };
        }

        return { name: 'DENIED (invalid policy)', restricted: true, denied: true, warning: false };
    }

    if (config.DefaultPolicyId) {
        var defaultPolicy = getEnabledPolicy(config.DefaultPolicyId);
        if (defaultPolicy) {
            return { name: defaultPolicy.Name + ' (default)', restricted: true, denied: false, warning: false };
        }

        return { name: 'Full Access (invalid default)', restricted: false, denied: false, warning: true };
    }

    return { name: 'Full Access', restricted: false, denied: false, warning: false };
}

function getPathKey(listName) {
    return listName === 'allowed' ? 'AllowedPathPrefixes' : 'BlockedPathPrefixes';
}

function getPathRows(paths) {
    return paths && paths.length ? paths.slice() : [''];
}

function getPathLabel(listName) {
    return listName === 'allowed' ? 'Allowed Path Prefixes' : 'Blocked Path Prefixes';
}

function getPathPlaceholder(listName) {
    return listName === 'allowed' ? '/path/to/allowed/' : '/path/to/blocked/';
}

function getPathHelpText(listName) {
    if (listName === 'allowed') {
        return 'Each prefix gets its own row. Leave the list empty to allow all.';
    }

    return 'Each prefix gets its own row. Matching files are always blocked.';
}

function getPathAddLabel(listName) {
    return listName === 'allowed' ? 'Add Allowed Path' : 'Add Blocked Path';
}

function buildPathField(policy, policyIndex, listName) {
    var key = getPathKey(listName);
    var rows = getPathRows(policy[key] || []);
    var label = getPathLabel(listName);
    var placeholder = getPathPlaceholder(listName);
    var helpText = getPathHelpText(listName);
    var addLabel = getPathAddLabel(listName);

    var rowHtml = rows.map(function (pathValue, rowIndex) {
        var showRemove = rows.length > 1 || Boolean(pathValue);

        return '<div class="qg-path-row">' +
            '<div class="inputContainer">' +
                '<input is="emby-input" type="text" class="qg-path-input policy-' + listName + '" ' +
                    'data-row-index="' + rowIndex + '" ' +
                    'value="' + escapeAttribute(pathValue) + '" ' +
                    'placeholder="' + placeholder + '" />' +
            '</div>' +
            (showRemove
                ? '<button type="button" class="qg-path-action qg-path-remove btnRemovePath" ' +
                    'data-index="' + policyIndex + '" ' +
                    'data-list="' + listName + '" ' +
                    'data-row="' + rowIndex + '" ' +
                    'aria-label="Remove ' + escapeAttribute(label.toLowerCase()) + ' row ' + (rowIndex + 1) + '">' +
                    'Remove</button>'
                : '') +
        '</div>';
    }).join('');

    return '<div class="qg-field">' +
        '<label class="qg-label">' + label + '</label>' +
        '<div class="qg-path-list">' + rowHtml + '</div>' +
        '<div class="qg-path-actions">' +
            '<button type="button" class="qg-path-action btnAddPath" ' +
                'data-index="' + policyIndex + '" ' +
                'data-list="' + listName + '">' +
                addLabel +
            '</button>' +
        '</div>' +
        '<div class="fieldDescription">' + helpText + '</div>' +
    '</div>';
}

function padPathRows(card, listName, paths) {
    var visibleCount = card ? card.querySelectorAll('.policy-' + listName).length : 0;
    while (paths.length < visibleCount) {
        paths.push('');
    }
    return paths;
}

function focusElement(view, selector) {
    var element = view.querySelector(selector);
    if (element) {
        element.focus();
    }
}

// --- Rendering ---

function renderAll(view) {
    renderPolicies(view);
    renderDefaultPolicyDropdown(view);
    renderUserAccess(view);
    view.querySelector('#defaultIntroPath').value = config.DefaultIntroVideoPath || '';
}

function renderPolicies(view) {
    var container = view.querySelector('#policiesContainer');
    container.innerHTML = '';

    if (config.Policies.length === 0) {
        container.innerHTML = '<p class="fieldDescription" style="font-style:italic;">No policies defined yet.</p>';
        return;
    }

    config.Policies.forEach(function (policy, index) {
        var card = document.createElement('div');
        card.className = 'qg-policy';
        card.dataset.index = index;
        card.innerHTML =
            '<div class="inputContainer" style="margin-bottom:.55em;">' +
                '<label class="inputLabel inputLabelUnfocused">Policy Name</label>' +
                '<input is="emby-input" type="text" class="policy-name" ' +
                    'value="' + escapeAttribute(policy.Name || 'Unnamed Policy') + '" ' +
                    'placeholder="Policy name" />' +
            '</div>' +
            buildPathField(policy, index, 'allowed') +
            buildPathField(policy, index, 'blocked') +
            '<div class="inputContainer">' +
                '<label class="inputLabel inputLabelUnfocused">Custom Intro Video</label>' +
                '<input is="emby-input" type="text" class="policy-intro" ' +
                    'value="' + escapeAttribute(policy.IntroVideoPath || '') + '" ' +
                    'placeholder="/media/intros/policy-intro.mp4" />' +
                '<div class="fieldDescription">Optional. Users under this policy see this intro instead of the default.</div>' +
            '</div>' +
            '<div class="checkboxContainer">' +
                '<label>' +
                    '<input is="emby-checkbox" type="checkbox" class="policy-enabled" ' +
                        (policy.Enabled !== false ? 'checked' : '') + ' />' +
                    '<span>Enabled</span>' +
                '</label>' +
            '</div>' +
            '<div class="qg-delete-row">' +
                '<button is="emby-button" type="button" class="raised qg-delete-btn btnDeletePolicy" ' +
                    'data-index="' + index + '">' +
                    '<span>Delete Policy</span>' +
                '</button>' +
            '</div>';
        container.appendChild(card);
    });

    view.querySelectorAll('.btnDeletePolicy').forEach(function (button) {
        button.addEventListener('click', function () {
            deletePolicy(view, parseInt(this.dataset.index, 10));
        });
    });

    view.querySelectorAll('.btnAddPath').forEach(function (button) {
        button.addEventListener('click', function () {
            addPath(view, parseInt(this.dataset.index, 10), this.dataset.list);
        });
    });

    view.querySelectorAll('.btnRemovePath').forEach(function (button) {
        button.addEventListener('click', function () {
            removePath(
                view,
                parseInt(this.dataset.index, 10),
                this.dataset.list,
                parseInt(this.dataset.row, 10)
            );
        });
    });
}

function renderDefaultPolicyDropdown(view) {
    var select = view.querySelector('#defaultPolicySelect');
    var current = config.DefaultPolicyId;
    select.innerHTML = '';

    if (hasInvalidDefaultPolicy()) {
        select.innerHTML += '<option value="' + escapeAttribute(current) + '" selected>' +
            'INVALID DEFAULT — currently Full Access' +
            '</option>';
    }

    select.innerHTML += '<option value=""' + (!current ? ' selected' : '') + '>(No default — Full Access)</option>';

    config.Policies.forEach(function (policy) {
        if (policy.Enabled !== false) {
            var option = document.createElement('option');
            option.value = policy.Id;
            option.textContent = policy.Name || 'Unnamed';
            if (current === policy.Id) {
                option.selected = true;
            }
            select.appendChild(option);
        }
    });
}

function renderUserAccess(view) {
    var container = view.querySelector('#userAccessContainer');
    if (users.length === 0) {
        container.innerHTML = '<p class="fieldDescription" style="font-style:italic;">No users found.</p>';
        return;
    }

    var defaultLabel = escapeHtml(getDefaultPolicyLabel());
    var html = '<table class="qg-user-table">' +
        '<thead><tr>' +
            '<th scope="col">User</th>' +
            '<th scope="col">Policy</th>' +
            '<th scope="col">Effective Access</th>' +
        '</tr></thead><tbody>';

    users.forEach(function (user) {
        var override = getUserOverride(user.Id);
        var overrideValue = override ? override.PolicyId : '';
        var stale = overrideValue && !isValidOverride(overrideValue);
        var effective = getEffectivePolicy(user.Id);
        var effectiveClass = effective.denied ? 'qg-effective-denied'
            : effective.warning ? 'qg-effective-restricted'
            : effective.restricted ? 'qg-effective-restricted'
            : 'qg-effective-full';

        html += '<tr>' +
            '<td><strong>' + escapeHtml(user.Name) + '</strong></td>' +
            '<td><div class="qg-select-wrap">' +
                '<select class="qg-user-select user-policy-select" ' +
                    'aria-label="Policy for ' + escapeAttribute(user.Name) + '" ' +
                    'data-userid="' + user.Id + '" ' +
                    'data-username="' + escapeAttribute(user.Name) + '">';

        if (stale) {
            html += '<option value="' + escapeAttribute(overrideValue) + '" selected>' +
                'DENIED — Invalid policy (change this)' +
                '</option>';
        }

        html += '<option value=""' + (!overrideValue ? ' selected' : '') + '>' +
                    'Use Default (' + defaultLabel + ')' +
                '</option>' +
                '<option value="__FULL_ACCESS__"' +
                    (overrideValue === '__FULL_ACCESS__' ? ' selected' : '') + '>' +
                    'Full Access' +
                '</option>';

        config.Policies.forEach(function (policy) {
            if (policy.Enabled !== false) {
                html += '<option value="' + escapeAttribute(policy.Id) + '"' +
                    (overrideValue === policy.Id ? ' selected' : '') + '>' +
                    escapeHtml(policy.Name || 'Unnamed') +
                    '</option>';
            }
        });

        html += '</select></div></td>' +
            '<td><span class="qg-effective ' + effectiveClass + '">' +
                escapeHtml(effective.name) +
            '</span></td></tr>';
    });

    html += '</tbody></table>';
    container.innerHTML = html;
}

// --- Data collection ---

function collectFromDOM(view) {
    view.querySelectorAll('#policiesContainer .qg-policy').forEach(function (card, index) {
        if (config.Policies[index]) {
            config.Policies[index].Name = card.querySelector('.policy-name').value;
            config.Policies[index].AllowedPathPrefixes = Array.prototype.map.call(
                card.querySelectorAll('.policy-allowed'),
                function (input) {
                    return input.value.trim();
                }
            ).filter(Boolean);
            config.Policies[index].BlockedPathPrefixes = Array.prototype.map.call(
                card.querySelectorAll('.policy-blocked'),
                function (input) {
                    return input.value.trim();
                }
            ).filter(Boolean);
            config.Policies[index].IntroVideoPath = card.querySelector('.policy-intro').value.trim();
            config.Policies[index].Enabled = card.querySelector('.policy-enabled').checked;
        }
    });

    config.DefaultPolicyId = view.querySelector('#defaultPolicySelect').value;
    config.DefaultIntroVideoPath = view.querySelector('#defaultIntroPath').value.trim();

    config.UserPolicies = [];
    view.querySelectorAll('.user-policy-select').forEach(function (select) {
        var value = select.value;
        if (value) {
            config.UserPolicies.push({
                UserId: select.dataset.userid,
                Username: select.dataset.username,
                PolicyId: value
            });
        }
    });
}

// --- Actions ---

function addPolicy(view) {
    var newIndex;

    if (!isLoaded) {
        return;
    }

    collectFromDOM(view);
    newIndex = config.Policies.length;

    config.Policies.push({
        Id: generateId(),
        Name: 'New Policy',
        AllowedPathPrefixes: [],
        BlockedPathPrefixes: [],
        Enabled: true,
        BlockedMessageHeader: 'Quality Restricted',
        BlockedMessageText: 'This quality version is not available for your account.',
        BlockedMessageTimeoutMs: 8000,
        IntroVideoPath: ''
    });

    renderAll(view);
    markDirty(view);
    focusElement(view, '.qg-policy[data-index="' + newIndex + '"] .policy-name');
}

function deletePolicy(view, index) {
    if (!isLoaded) {
        return;
    }

    if (!config.Policies[index]) {
        return;
    }

    if (confirm('Delete policy "' + (config.Policies[index].Name || 'Unnamed') + '"?')) {
        collectFromDOM(view);

        var deletedId = config.Policies[index].Id;
        config.Policies.splice(index, 1);

        if (config.DefaultPolicyId === deletedId) {
            config.DefaultPolicyId = '';
        }

        config.UserPolicies = config.UserPolicies.filter(function (assignment) {
            return assignment.PolicyId !== deletedId;
        });

        renderAll(view);
        markDirty(view);
    }
}

function addPath(view, policyIndex, listName) {
    var card = view.querySelector('.qg-policy[data-index="' + policyIndex + '"]');

    if (!isLoaded) {
        return;
    }

    collectFromDOM(view);
    if (!config.Policies[policyIndex]) {
        return;
    }

    var key = getPathKey(listName);
    var paths = config.Policies[policyIndex][key] || [];
    padPathRows(card, listName, paths);
    paths.push('');
    config.Policies[policyIndex][key] = paths;

    renderAll(view);
    markDirty(view);

    var inputs = view.querySelectorAll('.qg-policy[data-index="' + policyIndex + '"] .policy-' + listName);
    if (inputs.length) {
        inputs[inputs.length - 1].focus();
    }
}

function removePath(view, policyIndex, listName, rowIndex) {
    var card = view.querySelector('.qg-policy[data-index="' + policyIndex + '"]');

    if (!isLoaded) {
        return;
    }

    collectFromDOM(view);
    if (!config.Policies[policyIndex]) {
        return;
    }

    var key = getPathKey(listName);
    var paths = config.Policies[policyIndex][key] || [];
    padPathRows(card, listName, paths);

    if (rowIndex >= 0 && rowIndex < paths.length) {
        paths.splice(rowIndex, 1);
    }

    config.Policies[policyIndex][key] = paths;
    renderAll(view);
    markDirty(view);

    var remainingInputs = view.querySelectorAll('.qg-policy[data-index="' + policyIndex + '"] .policy-' + listName);
    if (remainingInputs.length) {
        remainingInputs[Math.max(0, rowIndex - 1)].focus();
    }
}

function loadConfig(view) {
    isLoaded = false;
    setStaticControlsEnabled(view, false);
    resetViewState(view);
    setLoadStatus(view, 'Loading configuration...');
    setSaveStatus(view, 'Waiting for configuration...');

    ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (cfg) {
        config = cfg || {};
        config.Policies = config.Policies || [];
        config.UserPolicies = config.UserPolicies || [];
        config.DefaultPolicyId = config.DefaultPolicyId || '';
        config.DefaultIntroVideoPath = config.DefaultIntroVideoPath || '';
        return ApiClient.getUsers();
    }).then(function (userList) {
        var enabledPolicies;

        users = userList || [];
        enabledPolicies = config.Policies.filter(function (policy) {
            return policy.Enabled !== false;
        }).length;

        isLoaded = true;
        renderAll(view);
        setStaticControlsEnabled(view, true);
        setLoadStatus(
            view,
            config.Policies.length + ' policies (' + enabledPolicies + ' enabled), ' + users.length + ' users',
            'success'
        );
        setSaveStatus(view, 'Changes are local until you click Save.');
    }).catch(function (err) {
        isLoaded = false;
        resetViewState(view);
        setStaticControlsEnabled(view, false);
        setLoadStatus(view, 'Error: ' + err, 'error');
        setSaveStatus(view, 'Unable to load configuration.', 'error');
    });
}

function saveConfig(view) {
    if (!isLoaded) {
        return;
    }

    collectFromDOM(view);
    setSaveStatus(view, 'Saving...', 'warning');

    ApiClient.updatePluginConfiguration(PLUGIN_ID, config).then(function () {
        setSaveStatus(view, 'Saved.', 'success');
        Dashboard.processPluginConfigurationUpdateResult();
    }).catch(function (err) {
        setSaveStatus(view, 'Error saving: ' + err, 'error');
        Dashboard.alert('Error saving: ' + err);
    });
}

// --- Controller entry point ---

export default function (view) {
    var form;

    if (view.dataset.qgInitialized === 'true') {
        return;
    }

    view.dataset.qgInitialized = 'true';
    form = view.querySelector('#QualityGateConfigForm');

    view.querySelector('#btnAddPolicy').addEventListener('click', function () {
        addPolicy(view);
    });

    form.addEventListener('submit', function (event) {
        event.preventDefault();
        saveConfig(view);
    });

    form.addEventListener('input', function () {
        markDirty(view);
    });

    form.addEventListener('change', function () {
        markDirty(view);
    });

    view.addEventListener('viewshow', function () {
        loadConfig(view);
    });
}
