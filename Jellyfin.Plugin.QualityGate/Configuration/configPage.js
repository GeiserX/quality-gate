const PLUGIN_ID = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890';
let config = { Policies: [], UserPolicies: [], DefaultPolicyId: '', DefaultIntroVideoPath: '' };
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
        config.DefaultIntroVideoPath = config.DefaultIntroVideoPath || '';
        return ApiClient.getUsers();
    }).then(function(userList) {
        console.log('[QualityGate] Users loaded: ' + (userList ? userList.length : 0));
        users = userList || [];
        statusEl.textContent = '✓ Loaded ' + config.Policies.length + ' policies, ' + users.length + ' users';
        statusEl.style.color = '#4caf50';
        renderAll(view);
    }).catch(function(err) {
        console.error('[QualityGate] Error:', err);
        statusEl.textContent = '✗ Error: ' + err;
        statusEl.style.color = '#f44336';
    });
}

function renderAll(view) {
    renderPolicies(view);
    renderDefaultPolicyDropdown(view);
    renderUserOverrides(view);
    renderUserDropdowns(view);
    // Set default intro path
    view.querySelector('#defaultIntroPath').value = config.DefaultIntroVideoPath || '';
}

function renderPolicies(view) {
    const container = view.querySelector('#policiesContainer');
    container.innerHTML = '';
    
    if (config.Policies.length === 0) {
        container.innerHTML = '<p class="fieldDescription" style="opacity: 0.6; font-style: italic;">No policies configured. Click "+ Add Policy" to create one.</p>';
        return;
    }
    
    config.Policies.forEach(function(policy, i) {
        const card = document.createElement('div');
        card.className = 'qg-policy-card';
        card.dataset.index = i;
        
        card.innerHTML = `
            <div class="qg-policy-header">
                <input is="emby-input" type="text" class="policy-name qg-policy-name-input" 
                       value="${escapeHtml(policy.Name || 'Unnamed Policy')}" 
                       placeholder="Policy name..." />
                <button is="emby-button" type="button" class="raised qg-delete-btn btnDeletePolicy" data-index="${i}">
                    <span>🗑️ Delete</span>
                </button>
            </div>
            
            <div class="qg-two-col">
                <div class="qg-input-group">
                    <label>✅ Allowed Paths</label>
                    <textarea is="emby-input" class="policy-allowed" rows="3" 
                              placeholder="/path/to/allowed/media/&#10;/another/allowed/path/">${escapeHtml((policy.AllowedPathPrefixes || []).join('\n'))}</textarea>
                    <div class="qg-hint">Files matching these paths will be accessible</div>
                </div>
                <div class="qg-input-group">
                    <label>🚫 Blocked Paths</label>
                    <textarea is="emby-input" class="policy-blocked" rows="3"
                              placeholder="/path/to/blocked/media/&#10;/another/blocked/path/">${escapeHtml((policy.BlockedPathPrefixes || []).join('\n'))}</textarea>
                    <div class="qg-hint">Files matching these paths will be blocked</div>
                </div>
            </div>
            
            <div class="qg-input-group">
                <label>💬 Blocked Message</label>
                <input is="emby-input" type="text" class="policy-msg" 
                       value="${escapeHtml(policy.BlockedMessageText || 'This quality is not available.')}"
                       placeholder="Message shown when playback is blocked" />
            </div>
            
            <div class="qg-intro-section">
                <div class="qg-input-group" style="margin-bottom: 0;">
                    <label>🎬 Custom Intro Video (optional)</label>
                    <input is="emby-input" type="text" class="policy-intro" 
                           value="${escapeHtml(policy.IntroVideoPath || '')}"
                           placeholder="/path/to/intro-720p.mp4 (leave empty for default intro)" />
                    <div class="qg-hint">Users under this policy will see this intro instead of the default one</div>
                </div>
            </div>
            
            <div class="qg-checkbox-row">
                <input is="emby-checkbox" type="checkbox" class="policy-enabled" id="enabled-${i}" ${policy.Enabled !== false ? 'checked' : ''} />
                <label for="enabled-${i}">Policy Enabled</label>
            </div>
        `;
        
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
        container.innerHTML = '<p class="fieldDescription" style="opacity: 0.6; font-style: italic;">No user overrides. All users use the default policy.</p>';
        return;
    }
    config.UserPolicies.forEach(function(up, i) {
        const user = users.find(function(u) { return u.Id === up.UserId; });
        const policy = config.Policies.find(function(p) { return p.Id === up.PolicyId; });
        const policyName = up.PolicyId === '__FULL_ACCESS__' ? '✅ Full Access' : (policy ? policy.Name : '❓ Unknown');
        const userName = user ? user.Name : (up.Username || up.UserId);
        const item = document.createElement('div');
        item.className = 'qg-override-item';
        item.innerHTML = `
            <span><strong>${escapeHtml(userName)}</strong> → <em>${escapeHtml(policyName)}</em></span>
            <button is="emby-button" type="button" class="raised btnRemoveOverride" 
                    style="background:#c62828;padding:0.3em 0.8em;border-radius:4px;" data-index="${i}">
                <span>✕</span>
            </button>
        `;
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
    policySel.innerHTML = '<option value="">-- Select Policy --</option><option value="__FULL_ACCESS__">✅ Full Access (no restrictions)</option>';
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
    view.querySelectorAll('#policiesContainer .qg-policy-card').forEach(function(card, i) {
        if (config.Policies[i]) {
            config.Policies[i].Name = card.querySelector('.policy-name').value;
            config.Policies[i].AllowedPathPrefixes = card.querySelector('.policy-allowed').value.split('\n').map(function(s){return s.trim();}).filter(Boolean);
            config.Policies[i].BlockedPathPrefixes = card.querySelector('.policy-blocked').value.split('\n').map(function(s){return s.trim();}).filter(Boolean);
            config.Policies[i].BlockedMessageText = card.querySelector('.policy-msg').value;
            config.Policies[i].IntroVideoPath = card.querySelector('.policy-intro').value.trim();
            config.Policies[i].Enabled = card.querySelector('.policy-enabled').checked;
        }
    });
    config.DefaultPolicyId = view.querySelector('#defaultPolicySelect').value;
    config.DefaultIntroVideoPath = view.querySelector('#defaultIntroPath').value.trim();
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
        BlockedMessageText: 'This quality version is not available for your account.',
        BlockedMessageTimeoutMs: 8000,
        IntroVideoPath: ''
    });
    renderAll(view);
}

function deletePolicy(view, index) {
    if (confirm('Delete this policy? This cannot be undone.')) {
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
        Dashboard.alert('Please select both a user and a policy');
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
    const statusEl = view.querySelector('#loadingStatus');
    statusEl.textContent = 'Saving...';
    statusEl.style.color = '#ffc107';
    
    ApiClient.updatePluginConfiguration(PLUGIN_ID, config).then(function() {
        statusEl.textContent = '✓ Configuration saved!';
        statusEl.style.color = '#4caf50';
        Dashboard.processPluginConfigurationUpdateResult();
    }).catch(function(err) {
        statusEl.textContent = '✗ Error saving: ' + err;
        statusEl.style.color = '#f44336';
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
