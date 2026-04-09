var PLUGIN_ID = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890';
var config = { Policies: [], UserPolicies: [], DefaultPolicyId: '', DefaultIntroVideoPath: '' };
var users = [];

function escapeHtml(text) {
    var div = document.createElement('div');
    div.textContent = text || '';
    return div.innerHTML;
}

function generateId() {
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
        var r = Math.random() * 16 | 0;
        return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
    });
}

function getDefaultPolicyLabel() {
    if (!config.DefaultPolicyId) {
        return 'Full Access';
    }
    var p = config.Policies.find(function (pol) { return pol.Id === config.DefaultPolicyId && pol.Enabled; });
    return p ? p.Name : 'Full Access';
}

function getUserOverride(userId) {
    return config.UserPolicies.find(function (up) { return up.UserId === userId; });
}

function isValidOverride(policyId) {
    if (!policyId || policyId === '__FULL_ACCESS__') {
        return true;
    }
    return config.Policies.some(function (p) { return p.Id === policyId && p.Enabled; });
}

function getEffectivePolicy(userId) {
    var override = getUserOverride(userId);
    if (override) {
        if (override.PolicyId === '__FULL_ACCESS__') {
            return { name: 'Full Access', restricted: false, denied: false };
        }
        var p = config.Policies.find(function (pol) { return pol.Id === override.PolicyId && pol.Enabled; });
        if (p) {
            return { name: p.Name, restricted: true, denied: false };
        }
        return { name: 'DENIED (invalid policy)', restricted: true, denied: true };
    }
    if (config.DefaultPolicyId) {
        var dp = config.Policies.find(function (pol) { return pol.Id === config.DefaultPolicyId && pol.Enabled; });
        if (dp) {
            return { name: dp.Name + ' (default)', restricted: true, denied: false };
        }
    }
    return { name: 'Full Access', restricted: false, denied: false };
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

    config.Policies.forEach(function (policy, i) {
        var div = document.createElement('div');
        div.className = 'qg-policy';
        div.dataset.index = i;
        div.innerHTML =
            '<div class="inputContainer" style="margin-bottom:.5em;">' +
                '<label class="inputLabel inputLabelUnfocused">Policy Name</label>' +
                '<input is="emby-input" type="text" class="policy-name" ' +
                    'value="' + escapeHtml(policy.Name || 'Unnamed Policy') + '" ' +
                    'placeholder="Policy name" />' +
            '</div>' +
            '<div class="qg-field">' +
                '<label class="qg-label">Allowed Path Prefixes</label>' +
                '<textarea class="qg-textarea policy-allowed" rows="3" ' +
                    'placeholder="/path/to/allowed/&#10;/another/path/">' +
                    escapeHtml((policy.AllowedPathPrefixes || []).join('\n')) +
                '</textarea>' +
                '<div class="fieldDescription">One per line. Only files under these paths are accessible. Leave empty to allow all.</div>' +
            '</div>' +
            '<div class="qg-field">' +
                '<label class="qg-label">Blocked Path Prefixes</label>' +
                '<textarea class="qg-textarea policy-blocked" rows="3" ' +
                    'placeholder="/path/to/blocked/&#10;/another/path/">' +
                    escapeHtml((policy.BlockedPathPrefixes || []).join('\n')) +
                '</textarea>' +
                '<div class="fieldDescription">One per line. Files under these paths are always blocked.</div>' +
            '</div>' +
            '<div class="inputContainer">' +
                '<label class="inputLabel inputLabelUnfocused">Custom Intro Video</label>' +
                '<input is="emby-input" type="text" class="policy-intro" ' +
                    'value="' + escapeHtml(policy.IntroVideoPath || '') + '" ' +
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
                '<button is="emby-button" type="button" class="raised btnDeletePolicy" ' +
                    'data-index="' + i + '" style="background:#c62828;">' +
                    '<span>Delete Policy</span>' +
                '</button>' +
            '</div>';
        container.appendChild(div);
    });

    view.querySelectorAll('.btnDeletePolicy').forEach(function (btn) {
        btn.addEventListener('click', function () {
            deletePolicy(view, parseInt(this.dataset.index));
        });
    });
}

function renderDefaultPolicyDropdown(view) {
    var select = view.querySelector('#defaultPolicySelect');
    var current = config.DefaultPolicyId;
    select.innerHTML = '<option value="">(No default — Full Access)</option>';
    config.Policies.forEach(function (p) {
        if (p.Enabled !== false) {
            var opt = document.createElement('option');
            opt.value = p.Id;
            opt.textContent = p.Name || 'Unnamed';
            if (current === p.Id) {
                opt.selected = true;
            }
            select.appendChild(opt);
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
            '<th>User</th>' +
            '<th>Policy</th>' +
            '<th>Effective Access</th>' +
        '</tr></thead><tbody>';

    users.forEach(function (user) {
        var override = getUserOverride(user.Id);
        var overrideValue = override ? override.PolicyId : '';
        var stale = overrideValue && !isValidOverride(overrideValue);
        var effective = getEffectivePolicy(user.Id);

        var effectiveClass = effective.denied ? 'qg-effective-denied'
            : effective.restricted ? 'qg-effective-restricted'
            : 'qg-effective-full';

        html += '<tr>' +
            '<td><strong>' + escapeHtml(user.Name) + '</strong></td>' +
            '<td>' +
                '<select class="qg-user-select user-policy-select" ' +
                    'data-userid="' + user.Id + '" ' +
                    'data-username="' + escapeHtml(user.Name) + '">';

        if (stale) {
            html += '<option value="' + escapeHtml(overrideValue) + '" selected>' +
                'DENIED — Invalid policy (change this)</option>';
        }

        html += '<option value=""' + (!overrideValue ? ' selected' : '') + '>' +
                    'Use Default (' + defaultLabel + ')</option>' +
                '<option value="__FULL_ACCESS__"' +
                    (overrideValue === '__FULL_ACCESS__' ? ' selected' : '') + '>' +
                    'Full Access</option>';

        config.Policies.forEach(function (p) {
            if (p.Enabled !== false) {
                html += '<option value="' + escapeHtml(p.Id) + '"' +
                    (overrideValue === p.Id ? ' selected' : '') + '>' +
                    escapeHtml(p.Name || 'Unnamed') + '</option>';
            }
        });

        html += '</select></td>' +
            '<td><span class="qg-effective ' + effectiveClass + '">' +
                escapeHtml(effective.name) + '</span></td></tr>';
    });

    html += '</tbody></table>';
    container.innerHTML = html;
}

// --- Data collection ---

function collectFromDOM(view) {
    view.querySelectorAll('#policiesContainer .qg-policy').forEach(function (card, i) {
        if (config.Policies[i]) {
            config.Policies[i].Name = card.querySelector('.policy-name').value;
            config.Policies[i].AllowedPathPrefixes = card.querySelector('.policy-allowed').value
                .split('\n').map(function (s) { return s.trim(); }).filter(Boolean);
            config.Policies[i].BlockedPathPrefixes = card.querySelector('.policy-blocked').value
                .split('\n').map(function (s) { return s.trim(); }).filter(Boolean);
            config.Policies[i].IntroVideoPath = card.querySelector('.policy-intro').value.trim();
            config.Policies[i].Enabled = card.querySelector('.policy-enabled').checked;
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
    collectFromDOM(view);
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
}

function deletePolicy(view, index) {
    if (confirm('Delete policy "' + (config.Policies[index].Name || 'Unnamed') + '"?')) {
        collectFromDOM(view);
        var id = config.Policies[index].Id;
        config.Policies.splice(index, 1);
        if (config.DefaultPolicyId === id) {
            config.DefaultPolicyId = '';
        }
        config.UserPolicies = config.UserPolicies.filter(function (up) { return up.PolicyId !== id; });
        renderAll(view);
    }
}

function loadConfig(view) {
    var statusEl = view.querySelector('#loadingStatus');
    statusEl.textContent = 'Loading...';
    statusEl.style.color = '';

    ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (cfg) {
        config = cfg || {};
        config.Policies = config.Policies || [];
        config.UserPolicies = config.UserPolicies || [];
        config.DefaultPolicyId = config.DefaultPolicyId || '';
        config.DefaultIntroVideoPath = config.DefaultIntroVideoPath || '';
        return ApiClient.getUsers();
    }).then(function (userList) {
        users = userList || [];
        statusEl.textContent = config.Policies.length + ' policies, ' + users.length + ' users';
        statusEl.style.color = '#66bb6a';
        renderAll(view);
    }).catch(function (err) {
        statusEl.textContent = 'Error: ' + err;
        statusEl.style.color = '#ef5350';
    });
}

function saveConfig(view) {
    collectFromDOM(view);
    var statusEl = view.querySelector('#loadingStatus');
    statusEl.textContent = 'Saving...';
    statusEl.style.color = '#ffc107';

    ApiClient.updatePluginConfiguration(PLUGIN_ID, config).then(function () {
        statusEl.textContent = 'Saved';
        statusEl.style.color = '#66bb6a';
        Dashboard.processPluginConfigurationUpdateResult();
    }).catch(function (err) {
        statusEl.textContent = 'Error: ' + err;
        statusEl.style.color = '#ef5350';
        Dashboard.alert('Error saving: ' + err);
    });
}

// --- Controller entry point ---

export default function (view) {
    view.querySelector('#btnAddPolicy').addEventListener('click', function () {
        addPolicy(view);
    });

    view.querySelector('#QualityGateConfigForm').addEventListener('submit', function (e) {
        e.preventDefault();
        saveConfig(view);
    });

    view.addEventListener('viewshow', function () {
        loadConfig(view);
    });
}
