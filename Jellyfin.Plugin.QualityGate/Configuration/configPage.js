const PLUGIN_ID = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890';
let config = { Policies: [], UserPolicies: [], DefaultPolicyId: '' };
let users = [];

function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text || '';
    return div.innerHTML;
}

function generateId() {
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function(c) {
        const r = Math.random() * 16 | 0, v = c === 'x' ? r : (r & 0x3 | 0x8);
        return v.toString(16);
    });
}

function loadConfig(view) {
    const statusEl = view.querySelector('#loadingStatus');
    statusEl.textContent = 'Loading...';
    console.log('[QualityGate] Loading config...');
    
    ApiClient.getPluginConfiguration(PLUGIN_ID).then(function(cfg) {
        console.log('[QualityGate] Config loaded');
        config = cfg || {};
        config.Policies = config.Policies || [];
        config.UserPolicies = config.UserPolicies || [];
        config.DefaultPolicyId = config.DefaultPolicyId || '';
        return ApiClient.getUsers();
    }).then(function(userList) {
        console.log('[QualityGate] Users loaded: ' + (userList ? userList.length : 0));
        users = userList || [];
        statusEl.textContent = 'Loaded ' + config.Policies.length + ' policies, ' + users.length + ' users';
        statusEl.style.color = '#4caf50';
        renderAll(view);
    }).catch(function(err) {
        console.error('[QualityGate] Error:', err);
        statusEl.textContent = 'Error: ' + err;
        statusEl.style.color = '#f44336';
    });
}

function renderAll(view) {
    renderPolicies(view);
    renderDefaultPolicyDropdown(view);
    renderUserOverrides(view);
    renderUserDropdowns(view);
}

function renderPolicies(view) {
    const container = view.querySelector('#policiesContainer');
    container.innerHTML = '';
    
    if (config.Policies.length === 0) {
        container.innerHTML = '<p class="fieldDescription">No policies. Click "+ Add Policy".</p>';
        return;
    }
    
    config.Policies.forEach(function(policy, i) {
        const card = document.createElement('div');
        card.className = 'cardBox';
        card.style.cssText = 'background: rgba(0,0,0,0.3); border-radius: 8px; padding: 1em; margin-bottom: 1em;';
        card.dataset.index = i;
        card.innerHTML = 
            '<div class="inputContainer"><label class="inputLabel">Name</label><input is="emby-input" type="text" class="policy-name" value="' + escapeHtml(policy.Name || '') + '" /></div>' +
            '<div class="inputContainer"><label class="inputLabel">Allowed Paths (one per line)</label><textarea is="emby-input" class="policy-allowed" rows="2">' + escapeHtml((policy.AllowedPathPrefixes || []).join('\n')) + '</textarea></div>' +
            '<div class="inputContainer"><label class="inputLabel">Blocked Paths (one per line)</label><textarea is="emby-input" class="policy-blocked" rows="2">' + escapeHtml((policy.BlockedPathPrefixes || []).join('\n')) + '</textarea></div>' +
            '<div class="inputContainer"><label class="inputLabel">Blocked Message</label><input is="emby-input" type="text" class="policy-msg" value="' + escapeHtml(policy.BlockedMessageText || 'This quality is not available.') + '" /></div>' +
            '<div class="checkboxContainer"><label><input is="emby-checkbox" type="checkbox" class="policy-enabled" ' + (policy.Enabled !== false ? 'checked' : '') + ' /><span>Enabled</span></label></div>' +
            '<button is="emby-button" type="button" class="raised btnDeletePolicy" style="background:#c62828;margin-top:0.5em;" data-index="' + i + '"><span>Delete</span></button>';
        container.appendChild(card);
    });
    
    view.querySelectorAll('.btnDeletePolicy').forEach(function(btn) {
        btn.addEventListener('click', function() {
            deletePolicy(view, parseInt(this.dataset.index));
        });
    });
}

function renderDefaultPolicyDropdown(view) {
    const select = view.querySelector('#defaultPolicySelect');
    select.innerHTML = '<option value="">(No default - Full Access)</option>';
    config.Policies.forEach(function(p) {
        if (p.Enabled !== false) {
            const opt = document.createElement('option');
            opt.value = p.Id;
            opt.textContent = p.Name || 'Unnamed';
            if (config.DefaultPolicyId === p.Id) opt.selected = true;
            select.appendChild(opt);
        }
    });
}

function renderUserOverrides(view) {
    const container = view.querySelector('#userOverridesContainer');
    container.innerHTML = '';
    if (config.UserPolicies.length === 0) {
        container.innerHTML = '<p class="fieldDescription">No overrides.</p>';
        return;
    }
    config.UserPolicies.forEach(function(up, i) {
        const user = users.find(function(u) { return u.Id === up.UserId; });
        const policy = config.Policies.find(function(p) { return p.Id === up.PolicyId; });
        const policyName = up.PolicyId === '__FULL_ACCESS__' ? '✅ Full Access' : (policy ? policy.Name : '?');
        const userName = user ? user.Name : (up.Username || up.UserId);
        const item = document.createElement('div');
        item.style.cssText = 'display:flex;align-items:center;padding:0.5em;background:rgba(0,0,0,0.2);border-radius:4px;margin-bottom:0.5em;';
        item.innerHTML = '<span style="flex:1;"><strong>' + escapeHtml(userName) + '</strong> → ' + escapeHtml(policyName) + '</span><button is="emby-button" type="button" class="raised btnRemoveOverride" style="background:#c62828;padding:0.3em 0.6em;" data-index="' + i + '"><span>✕</span></button>';
        container.appendChild(item);
    });
    view.querySelectorAll('.btnRemoveOverride').forEach(function(btn) {
        btn.addEventListener('click', function() {
            removeOverride(view, parseInt(this.dataset.index));
        });
    });
}

function renderUserDropdowns(view) {
    const userSel = view.querySelector('#newOverrideUser');
    userSel.innerHTML = '<option value="">-- Select User --</option>';
    users.forEach(function(u) {
        const opt = document.createElement('option');
        opt.value = u.Id;
        opt.textContent = u.Name;
        userSel.appendChild(opt);
    });
    
    const policySel = view.querySelector('#newOverridePolicy');
    policySel.innerHTML = '<option value="">-- Select Policy --</option><option value="__FULL_ACCESS__">✅ Full Access</option>';
    config.Policies.forEach(function(p) {
        if (p.Enabled !== false) {
            const opt = document.createElement('option');
            opt.value = p.Id;
            opt.textContent = p.Name || 'Unnamed';
            policySel.appendChild(opt);
        }
    });
}

function collectFromDOM(view) {
    view.querySelectorAll('#policiesContainer .cardBox').forEach(function(card, i) {
        if (config.Policies[i]) {
            config.Policies[i].Name = card.querySelector('.policy-name').value;
            config.Policies[i].AllowedPathPrefixes = card.querySelector('.policy-allowed').value.split('\n').map(function(s){return s.trim();}).filter(Boolean);
            config.Policies[i].BlockedPathPrefixes = card.querySelector('.policy-blocked').value.split('\n').map(function(s){return s.trim();}).filter(Boolean);
            config.Policies[i].BlockedMessageText = card.querySelector('.policy-msg').value;
            config.Policies[i].Enabled = card.querySelector('.policy-enabled').checked;
        }
    });
    config.DefaultPolicyId = view.querySelector('#defaultPolicySelect').value;
}

function addPolicy(view) {
    collectFromDOM(view);
    config.Policies.push({
        Id: generateId(),
        Name: 'New Policy',
        AllowedPathPrefixes: [],
        BlockedPathPrefixes: [],
        Enabled: true,
        BlockedMessageHeader: 'Quality Restricted',
        BlockedMessageText: 'This quality is not available.',
        BlockedMessageTimeoutMs: 8000
    });
    renderAll(view);
}

function deletePolicy(view, index) {
    if (confirm('Delete this policy?')) {
        collectFromDOM(view);
        const id = config.Policies[index].Id;
        config.Policies.splice(index, 1);
        if (config.DefaultPolicyId === id) config.DefaultPolicyId = '';
        config.UserPolicies = config.UserPolicies.filter(function(up) { return up.PolicyId !== id; });
        renderAll(view);
    }
}

function addOverride(view) {
    const userId = view.querySelector('#newOverrideUser').value;
    const policyId = view.querySelector('#newOverridePolicy').value;
    if (!userId || !policyId) { 
        Dashboard.alert('Please select both user and policy');
        return; 
    }
    collectFromDOM(view);
    const user = users.find(function(u) { return u.Id === userId; });
    config.UserPolicies = config.UserPolicies.filter(function(up) { return up.UserId !== userId; });
    config.UserPolicies.push({ UserId: userId, Username: user ? user.Name : '', PolicyId: policyId });
    view.querySelector('#newOverrideUser').value = '';
    view.querySelector('#newOverridePolicy').value = '';
    renderAll(view);
}

function removeOverride(view, index) {
    collectFromDOM(view);
    config.UserPolicies.splice(index, 1);
    renderAll(view);
}

function saveConfig(view) {
    collectFromDOM(view);
    console.log('[QualityGate] Saving config...');
    ApiClient.updatePluginConfiguration(PLUGIN_ID, config).then(function() {
        Dashboard.processPluginConfigurationUpdateResult();
    }).catch(function(err) {
        Dashboard.alert('Error saving: ' + err);
    });
}

export default function(view, params) {
    console.log('[QualityGate] Controller loaded');
    
    view.addEventListener('viewshow', function() {
        console.log('[QualityGate] viewshow event');
        
        view.querySelector('#btnAddPolicy').addEventListener('click', function() {
            addPolicy(view);
        });
        
        view.querySelector('#btnAddOverride').addEventListener('click', function() {
            addOverride(view);
        });
        
        view.querySelector('#QualityGateConfigForm').addEventListener('submit', function(e) {
            e.preventDefault();
            saveConfig(view);
            return false;
        });
        
        loadConfig(view);
    });
}
