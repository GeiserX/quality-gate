(function() {
    'use strict';
    
    var PLUGIN_ID = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890';
    var config = { Policies: [], UserPolicies: [], DefaultPolicyId: '' };
    var users = [];
    
    function log(msg) {
        console.log('[QualityGate] ' + msg);
    }
    
    function escapeHtml(text) {
        var div = document.createElement('div');
        div.textContent = text || '';
        return div.innerHTML;
    }
    
    function generateId() {
        return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function(c) {
            var r = Math.random() * 16 | 0, v = c === 'x' ? r : (r & 0x3 | 0x8);
            return v.toString(16);
        });
    }
    
    function loadConfig() {
        var statusEl = document.getElementById('loadingStatus');
        if (!statusEl) { log('No status element'); return; }
        statusEl.textContent = 'Loading...';
        log('Loading config...');
        
        ApiClient.getPluginConfiguration(PLUGIN_ID).then(function(cfg) {
            log('Config loaded');
            config = cfg || {};
            config.Policies = config.Policies || [];
            config.UserPolicies = config.UserPolicies || [];
            config.DefaultPolicyId = config.DefaultPolicyId || '';
            return ApiClient.getUsers();
        }).then(function(userList) {
            log('Users loaded: ' + (userList ? userList.length : 0));
            users = userList || [];
            statusEl.textContent = 'Loaded ' + config.Policies.length + ' policies, ' + users.length + ' users';
            statusEl.style.color = '#4caf50';
            renderAll();
        }).catch(function(err) {
            log('Error: ' + err);
            statusEl.textContent = 'Error: ' + err;
            statusEl.style.color = '#f44336';
        });
    }
    
    function renderAll() {
        renderPolicies();
        renderDefaultPolicyDropdown();
        renderUserOverrides();
        renderUserDropdowns();
    }
    
    function renderPolicies() {
        var container = document.getElementById('policiesContainer');
        if (!container) return;
        container.innerHTML = '';
        
        if (config.Policies.length === 0) {
            container.innerHTML = '<p class="fieldDescription">No policies. Click "+ Add Policy".</p>';
            return;
        }
        
        config.Policies.forEach(function(policy, i) {
            var card = document.createElement('div');
            card.className = 'cardBox';
            card.style.cssText = 'background: rgba(0,0,0,0.3); border-radius: 8px; padding: 1em; margin-bottom: 1em;';
            card.dataset.index = i;
            card.innerHTML = 
                '<div class="inputContainer"><label class="inputLabel">Name</label><input is="emby-input" type="text" class="policy-name" value="' + escapeHtml(policy.Name || '') + '" /></div>' +
                '<div class="inputContainer"><label class="inputLabel">Allowed Paths (one per line)</label><textarea is="emby-input" class="policy-allowed" rows="2">' + escapeHtml((policy.AllowedPathPrefixes || []).join('\n')) + '</textarea></div>' +
                '<div class="inputContainer"><label class="inputLabel">Blocked Paths (one per line)</label><textarea is="emby-input" class="policy-blocked" rows="2">' + escapeHtml((policy.BlockedPathPrefixes || []).join('\n')) + '</textarea></div>' +
                '<div class="inputContainer"><label class="inputLabel">Blocked Message</label><input is="emby-input" type="text" class="policy-msg" value="' + escapeHtml(policy.BlockedMessageText || 'This quality is not available.') + '" /></div>' +
                '<div class="checkboxContainer"><label><input is="emby-checkbox" type="checkbox" class="policy-enabled" ' + (policy.Enabled !== false ? 'checked' : '') + ' /><span>Enabled</span></label></div>' +
                '<button is="emby-button" type="button" class="raised btnDeletePolicy" style="background:#c62828;margin-top:0.5em;"><span>Delete</span></button>';
            container.appendChild(card);
        });
        
        document.querySelectorAll('.btnDeletePolicy').forEach(function(btn, i) {
            btn.onclick = function() { deletePolicy(i); };
        });
    }
    
    function renderDefaultPolicyDropdown() {
        var select = document.getElementById('defaultPolicySelect');
        if (!select) return;
        select.innerHTML = '<option value="">(No default - Full Access)</option>';
        config.Policies.forEach(function(p) {
            if (p.Enabled !== false) {
                var opt = document.createElement('option');
                opt.value = p.Id;
                opt.textContent = p.Name || 'Unnamed';
                if (config.DefaultPolicyId === p.Id) opt.selected = true;
                select.appendChild(opt);
            }
        });
    }
    
    function renderUserOverrides() {
        var container = document.getElementById('userOverridesContainer');
        if (!container) return;
        container.innerHTML = '';
        if (config.UserPolicies.length === 0) {
            container.innerHTML = '<p class="fieldDescription">No overrides.</p>';
            return;
        }
        config.UserPolicies.forEach(function(up, i) {
            var user = users.find(function(u) { return u.Id === up.UserId; });
            var policy = config.Policies.find(function(p) { return p.Id === up.PolicyId; });
            var policyName = up.PolicyId === '__FULL_ACCESS__' ? '✅ Full Access' : (policy ? policy.Name : '?');
            var userName = user ? user.Name : (up.Username || up.UserId);
            var item = document.createElement('div');
            item.style.cssText = 'display:flex;align-items:center;padding:0.5em;background:rgba(0,0,0,0.2);border-radius:4px;margin-bottom:0.5em;';
            item.innerHTML = '<span style="flex:1;"><strong>' + escapeHtml(userName) + '</strong> → ' + escapeHtml(policyName) + '</span><button is="emby-button" type="button" class="raised btnRemoveOverride" style="background:#c62828;padding:0.3em 0.6em;"><span>✕</span></button>';
            container.appendChild(item);
        });
        document.querySelectorAll('.btnRemoveOverride').forEach(function(btn, i) {
            btn.onclick = function() { removeOverride(i); };
        });
    }
    
    function renderUserDropdowns() {
        var userSel = document.getElementById('newOverrideUser');
        if (!userSel) return;
        userSel.innerHTML = '<option value="">-- Select User --</option>';
        users.forEach(function(u) {
            var opt = document.createElement('option');
            opt.value = u.Id;
            opt.textContent = u.Name;
            userSel.appendChild(opt);
        });
        
        var policySel = document.getElementById('newOverridePolicy');
        if (!policySel) return;
        policySel.innerHTML = '<option value="">-- Select Policy --</option><option value="__FULL_ACCESS__">✅ Full Access</option>';
        config.Policies.forEach(function(p) {
            if (p.Enabled !== false) {
                var opt = document.createElement('option');
                opt.value = p.Id;
                opt.textContent = p.Name || 'Unnamed';
                policySel.appendChild(opt);
            }
        });
    }
    
    function collectFromDOM() {
        document.querySelectorAll('#policiesContainer .cardBox').forEach(function(card, i) {
            if (config.Policies[i]) {
                config.Policies[i].Name = card.querySelector('.policy-name').value;
                config.Policies[i].AllowedPathPrefixes = card.querySelector('.policy-allowed').value.split('\n').map(function(s){return s.trim();}).filter(Boolean);
                config.Policies[i].BlockedPathPrefixes = card.querySelector('.policy-blocked').value.split('\n').map(function(s){return s.trim();}).filter(Boolean);
                config.Policies[i].BlockedMessageText = card.querySelector('.policy-msg').value;
                config.Policies[i].Enabled = card.querySelector('.policy-enabled').checked;
            }
        });
        var defSel = document.getElementById('defaultPolicySelect');
        if (defSel) config.DefaultPolicyId = defSel.value;
    }
    
    function addPolicy() {
        collectFromDOM();
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
        renderAll();
    }
    
    function deletePolicy(index) {
        if (confirm('Delete this policy?')) {
            collectFromDOM();
            var id = config.Policies[index].Id;
            config.Policies.splice(index, 1);
            if (config.DefaultPolicyId === id) config.DefaultPolicyId = '';
            config.UserPolicies = config.UserPolicies.filter(function(up) { return up.PolicyId !== id; });
            renderAll();
        }
    }
    
    function addOverride() {
        var userSel = document.getElementById('newOverrideUser');
        var policySel = document.getElementById('newOverridePolicy');
        if (!userSel || !policySel) return;
        var userId = userSel.value;
        var policyId = policySel.value;
        if (!userId || !policyId) { alert('Select both user and policy'); return; }
        collectFromDOM();
        var user = users.find(function(u) { return u.Id === userId; });
        config.UserPolicies = config.UserPolicies.filter(function(up) { return up.UserId !== userId; });
        config.UserPolicies.push({ UserId: userId, Username: user ? user.Name : '', PolicyId: policyId });
        userSel.value = '';
        policySel.value = '';
        renderAll();
    }
    
    function removeOverride(index) {
        collectFromDOM();
        config.UserPolicies.splice(index, 1);
        renderAll();
    }
    
    function saveConfig() {
        collectFromDOM();
        log('Saving...');
        ApiClient.updatePluginConfiguration(PLUGIN_ID, config).then(function() {
            Dashboard.processPluginConfigurationUpdateResult();
        }).catch(function(err) {
            alert('Error: ' + err);
        });
    }
    
    function init() {
        log('Initializing...');
        var addPolicyBtn = document.getElementById('btnAddPolicy');
        var addOverrideBtn = document.getElementById('btnAddOverride');
        var form = document.getElementById('QualityGateConfigForm');
        
        if (addPolicyBtn) addPolicyBtn.onclick = addPolicy;
        if (addOverrideBtn) addOverrideBtn.onclick = addOverride;
        if (form) form.onsubmit = function(e) { e.preventDefault(); saveConfig(); return false; };
        
        loadConfig();
    }
    
    // Wait for DOM and ApiClient
    function waitAndInit() {
        if (typeof ApiClient !== 'undefined' && document.getElementById('QualityGateConfigPage')) {
            init();
        } else {
            setTimeout(waitAndInit, 100);
        }
    }
    
    log('Script loaded');
    waitAndInit();
})();

