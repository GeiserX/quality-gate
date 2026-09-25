var PLUGIN_ID = '9cab70ca-0af3-4d3a-adab-6a0df2496a33';
var FULL_ACCESS_POLICY_ID = '__FULL_ACCESS__';
var config = { Policies: [], UserPolicies: [], DefaultPolicyId: '', DefaultIntroVideoPath: '', ApiKeyPolicyId: '' };
var users = [];
var isLoaded = false;
var userAccessPageSize = 10;
var userAccessPage = 0;
var userAccessSortColumn = 'name';
var userAccessSortDirection = 'asc';

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

function upgradeNativeWidgets(root) {
    if (!window.customElements || typeof window.customElements.upgrade !== 'function' || !root) {
        return;
    }

    root.querySelectorAll('[is="emby-button"], [is="emby-select"], [is="emby-checkbox"], [is="emby-input"]').forEach(function (element) {
        window.customElements.upgrade(element);
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
    var apiKeyPolicySelect = view.querySelector('#apiKeyPolicySelect');
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

    if (apiKeyPolicySelect) {
        apiKeyPolicySelect.disabled = !enabled;
    }

    if (defaultIntroPath) {
        defaultIntroPath.disabled = !enabled;
    }
}

function resetViewState(view) {
    view.querySelector('#policiesContainer').innerHTML = '';
    view.querySelector('#userAccessContainer').innerHTML = '';
    view.querySelector('#defaultPolicySelect').innerHTML = '<option value="">(No default - Full Access)</option>';
    view.querySelector('#apiKeyPolicySelect').innerHTML = '<option value="">(Uncapped)</option>';
    view.querySelector('#defaultIntroPath').value = '';
}

function getEnabledPolicy(policyId) {
    return config.Policies.find(function (policy) {
        return policy.Id === policyId && policy.Enabled !== false;
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
    return policy ? (policy.Name || 'Unnamed Policy') : 'DENIED (invalid default)';
}

function getUserOverride(userId) {
    return config.UserPolicies.find(function (assignment) {
        return assignment.UserId === userId;
    });
}

function isValidOverride(policyId) {
    if (!policyId || policyId === FULL_ACCESS_POLICY_ID) {
        return true;
    }

    return Boolean(getEnabledPolicy(policyId));
}

function getEffectivePolicy(userId) {
    var override = getUserOverride(userId);
    var defaultPolicy;
    var overridePolicy;

    if (override) {
        if (override.PolicyId === FULL_ACCESS_POLICY_ID) {
            return { name: 'Full Access', restricted: false, denied: false, warning: false };
        }

        overridePolicy = getEnabledPolicy(override.PolicyId);
        if (overridePolicy) {
            return {
                name: (overridePolicy.Name || 'Unnamed Policy') + ' (override)',
                restricted: true,
                denied: false,
                warning: false
            };
        }

        return { name: 'Denied (invalid policy)', restricted: true, denied: true, warning: false };
    }

    if (config.DefaultPolicyId) {
        defaultPolicy = getEnabledPolicy(config.DefaultPolicyId);
        if (defaultPolicy) {
            return {
                name: (defaultPolicy.Name || 'Unnamed Policy') + ' (default)',
                restricted: true,
                denied: false,
                warning: false
            };
        }

        return { name: 'DENIED (invalid default)', restricted: true, denied: true, warning: true };
    }

    return { name: 'Full Access', restricted: false, denied: false, warning: false };
}

function getFieldKey(listName) {
    return listName === 'fn-allowed' ? 'AllowedFilenamePatterns' : 'BlockedFilenamePatterns';
}

function getPathRows(paths) {
    return paths && paths.length ? paths.slice() : [''];
}

function getPathTitle(listName) {
    return listName === 'fn-allowed' ? 'Allowed Filename Patterns' : 'Blocked Filename Patterns';
}

function getPathRowLabel(listName) {
    return listName === 'fn-allowed' ? 'Allowed Pattern' : 'Blocked Pattern';
}

function getPathPlaceholder(listName) {
    return listName === 'fn-allowed' ? '- 720p|- 1080p' : '- 2160p|- 4K';
}

function getPathHelpText(listName) {
    return listName === 'fn-allowed'
        ? 'Regex matched against the filename (not full path). Leave empty to allow all filenames.'
        : 'Regex matched against the filename (not full path). Matching files are always blocked.';
}

function getPathAddLabel(listName) {
    return listName === 'fn-allowed' ? 'Add Allowed Pattern' : 'Add Blocked Pattern';
}

function getEmptyState(message) {
    return '<div class="qg-empty-state"><p class="fieldDescription" style="margin:0;font-style:italic;">' +
        escapeHtml(message) +
        '</p></div>';
}

/** The heights the resolution dropdowns offer out of the box. */
var HEIGHT_PRESETS = [480, 720, 1080, 1440, 2160];

/**
 * Reads a configured height as a whole number of pixels.
 * Anything missing, unparseable or not positive means "no cap", the same as the server's 0.
 */
export function toHeight(value) {
    var height = parseInt(value, 10);

    return isFinite(height) && height > 0 ? height : 0;
}

/**
 * The heights a resolution dropdown must offer for an already-configured value.
 *
 * MaxHeight is a plain int on the server and nothing holds it to the presets: a config
 * written by hand, by an older build or by a future one can say 1000. A dropdown with no
 * option for it would leave the browser on its first option, and the next save would write
 * that back — silently lifting the cap from every user on the policy. So the configured
 * value is always in the list, whatever it is.
 */
export function heightChoices(configured) {
    var height = toHeight(configured);
    var choices = HEIGHT_PRESETS.slice();

    if (height > 0 && choices.indexOf(height) === -1) {
        choices.push(height);
        choices.sort(function (a, b) {
            return a - b;
        });
    }

    return choices;
}

function getHeightLabel(height) {
    return height === 2160 ? '4K' : height + 'p';
}

function buildOption(value, label, selected) {
    return '<option value="' + value + '"' + (selected ? ' selected' : '') + '>' + label + '</option>';
}

/** Builds the Maximum Resolution options, with the policy's own height always among them. */
export function buildMaxHeightOptions(configured) {
    var selected = toHeight(configured);
    var html = buildOption(0, 'No limit', selected === 0);

    heightChoices(configured).forEach(function (height) {
        html += buildOption(height, getHeightLabel(height), height === selected);
    });

    return html;
}

/** Builds the If No Match Found options, with the policy's own fallback height always among them. */
export function buildFallbackOptions(policy) {
    var enabled = !!policy.FallbackTranscode;
    var selected = toHeight(policy.FallbackMaxHeight);
    var html = buildOption('off', 'Block playback', !enabled);

    heightChoices(policy.FallbackMaxHeight).forEach(function (height) {
        html += buildOption(height, 'Transcode to ' + getHeightLabel(height), enabled && height === selected);
    });

    return html + buildOption(0, 'Transcode (no resolution cap)', enabled && selected === 0);
}

function buildPathField(policy, policyIndex, listName) {
    var key = getFieldKey(listName);
    var rows = getPathRows(policy[key] || []);
    var title = getPathTitle(listName);
    var rowLabel = getPathRowLabel(listName);
    var placeholder = getPathPlaceholder(listName);
    var helpText = getPathHelpText(listName);
    var addLabel = getPathAddLabel(listName);
    var groupClass = listName === 'fn-allowed' ? 'qg-path-group-fn-allowed' : 'qg-path-group-fn-blocked';

    var rowHtml = rows.map(function (pathValue, rowIndex) {
        var inputId = 'policy-' + policyIndex + '-' + listName + '-row-' + rowIndex;
        var showRemove = rows.length > 1 && rowIndex > 0;

        return '<div class="qg-path-row">' +
            '<div class="inputContainer">' +
                '<label class="inputLabel inputLabelUnfocused" for="' + inputId + '">' +
                    rowLabel + ' ' + (rowIndex + 1) +
                '</label>' +
                '<input type="text" id="' + inputId + '" class="emby-input qg-path-input policy-' + listName + '" ' +
                    'data-row-index="' + rowIndex + '" ' +
                    'value="' + escapeAttribute(pathValue) + '" ' +
                    'placeholder="' + escapeAttribute(placeholder) + '" />' +
            '</div>' +
            (showRemove
                ? '<button is="emby-button" type="button" class="raised btnRemovePath qg-path-row-action" ' +
                    'data-index="' + policyIndex + '" ' +
                    'data-list="' + listName + '" ' +
                    'data-row="' + rowIndex + '" ' +
                    'aria-label="Remove ' + escapeAttribute(rowLabel.toLowerCase()) + ' ' + (rowIndex + 1) + '">' +
                    '<span>Remove Pattern</span>' +
                  '</button>'
                : '') +
        '</div>';
    }).join('');

    return '<div class="qg-path-group ' + groupClass + '">' +
        '<div class="qg-path-group-head">' +
            '<div>' +
                '<h3 class="qg-path-group-title">' + title + '</h3>' +
                '<div class="fieldDescription">' + helpText + '</div>' +
            '</div>' +
        '</div>' +
        '<div class="qg-path-list">' + rowHtml + '</div>' +
        '<div class="qg-path-actions">' +
            '<button is="emby-button" type="button" class="raised btnAddPath" ' +
                'data-index="' + policyIndex + '" ' +
                'data-list="' + listName + '">' +
                '<span>' + addLabel + '</span>' +
            '</button>' +
        '</div>' +
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

function getEffectiveClass(effective) {
    if (effective.denied) {
        return 'qg-effective-denied';
    }

    if (effective.warning) {
        return 'qg-effective-warning';
    }

    if (effective.restricted) {
        return 'qg-effective-restricted';
    }

    return 'qg-effective-full';
}

function getAssignedPolicySummary(userId) {
    var override = getUserOverride(userId);
    var policy;

    if (!override) {
        return 'Use Default (' + getDefaultPolicyLabel() + ')';
    }

    if (override.PolicyId === FULL_ACCESS_POLICY_ID) {
        return 'Full Access';
    }

    policy = getEnabledPolicy(override.PolicyId);
    if (policy) {
        return policy.Name || 'Unnamed Policy';
    }

    return 'Denied - invalid policy';
}

function compareText(a, b) {
    return String(a || '').localeCompare(String(b || ''), undefined, { sensitivity: 'base', numeric: true });
}

function getSortedUsers() {
    var sorted = users.slice();

    sorted.sort(function (left, right) {
        var leftValue;
        var rightValue;
        var result;

        if (userAccessSortColumn === 'policy') {
            leftValue = getAssignedPolicySummary(left.Id);
            rightValue = getAssignedPolicySummary(right.Id);
        } else if (userAccessSortColumn === 'effective') {
            leftValue = getEffectivePolicy(left.Id).name;
            rightValue = getEffectivePolicy(right.Id).name;
        } else {
            leftValue = left.Name || '';
            rightValue = right.Name || '';
        }

        result = compareText(leftValue, rightValue);
        if (result === 0) {
            result = compareText(left.Name || '', right.Name || '');
        }

        return userAccessSortDirection === 'desc' ? result * -1 : result;
    });

    return sorted;
}

function getSortIndicator(column) {
    if (userAccessSortColumn !== column) {
        return '↕';
    }

    return userAccessSortDirection === 'asc' ? '↑' : '↓';
}

function setUserAccessSort(column) {
    if (userAccessSortColumn === column) {
        userAccessSortDirection = userAccessSortDirection === 'asc' ? 'desc' : 'asc';
    } else {
        userAccessSortColumn = column;
        userAccessSortDirection = 'asc';
    }

    userAccessPage = 0;
}

function setUserPolicyAssignment(userId, username, policyId) {
    config.UserPolicies = config.UserPolicies.filter(function (assignment) {
        return assignment.UserId !== userId;
    });

    if (policyId) {
        config.UserPolicies.push({
            UserId: userId,
            Username: username,
            PolicyId: policyId
        });
    }
}

function getUserAccessPageCount() {
    return Math.max(1, Math.ceil(users.length / userAccessPageSize));
}

function clampUserAccessPage() {
    var lastPage = getUserAccessPageCount() - 1;
    if (userAccessPage < 0) {
        userAccessPage = 0;
    }
    if (userAccessPage > lastPage) {
        userAccessPage = lastPage;
    }
}

function renderAll(view) {
    renderPolicies(view);
    renderDefaultPolicyDropdown(view);
    renderApiKeyPolicyDropdown(view);
    renderUserAccess(view);
    view.querySelector('#defaultIntroPath').value = config.DefaultIntroVideoPath || '';
    view.querySelector('#enableVersionGrouping').checked = Boolean(config.EnableVersionGrouping);
    view.querySelector('#versionGroupingSuffixes').value = (config.VersionGroupingSuffixes || []).join('\n');
    view.querySelector('#versionGroupingRoots').value = (config.VersionGroupingRoots || []).join('\n');
    upgradeNativeWidgets(view);
}

function renderPolicies(view) {
    var container = view.querySelector('#policiesContainer');

    container.innerHTML = '';
    if (config.Policies.length === 0) {
        container.innerHTML = getEmptyState('No policies defined yet. Click "Add Policy" to create one.');
        return;
    }

    config.Policies.forEach(function (policy, index) {
        var nameId = 'policy-name-' + index;
        var introId = 'policy-intro-' + index;
        var enabledId = 'policy-enabled-' + index;
        var card = document.createElement('fieldset');

        card.className = 'qg-policy-card';
        card.dataset.index = index;
        card.innerHTML =
            '<legend class="qg-policy-legend">Policy ' + (index + 1) + '</legend>' +
            '<div class="qg-policy-card-header">' +
                '<div class="qg-policy-heading">' +
                    '<div class="qg-policy-kicker">Define access rules for this policy.</div>' +
                    '<div class="inputContainer qg-policy-name-field">' +
                        '<label class="inputLabel inputLabelUnfocused" for="' + nameId + '">Policy Name</label>' +
                        '<input type="text" id="' + nameId + '" class="emby-input policy-name" ' +
                            'value="' + escapeAttribute(policy.Name || '') + '" ' +
                            'placeholder="Policy name" />' +
                    '</div>' +
                '</div>' +
            '</div>' +
            '<div class="qg-policy-section">' +
                '<h3 class="qg-policy-section-title">Filename Pattern Rules (Regex)</h3>' +
                '<div class="fieldDescription" style="margin-bottom:.8rem">' +
                    'Match against the filename only (e.g. <code>Movie (2021) - 1080p.mp4</code>). ' +
                    'Supports <a href="https://jellyfin.org/docs/general/server/media/movies/#multiple-versions" target="_blank" rel="noopener">Jellyfin multi-version naming</a>. ' +
                    'Patterns are case-insensitive regex.' +
                '</div>' +
                '<div class="qg-policy-grid">' +
                    buildPathField(policy, index, 'fn-allowed') +
                    buildPathField(policy, index, 'fn-blocked') +
                '</div>' +
            '</div>' +
            '<div class="qg-policy-section">' +
                '<h3 class="qg-policy-section-title">Playback Behavior</h3>' +
                '<div class="qg-policy-footer">' +
                    '<div class="selectContainer qg-policy-maxheight-field">' +
                        '<label class="selectLabel" for="policy-maxheight-' + index + '">Maximum Resolution</label>' +
                        '<select is="emby-select" id="policy-maxheight-' + index + '" class="emby-select policy-max-height">' +
                            buildMaxHeightOptions(policy.MaxHeight) +
                        '</select>' +
                        '<div class="fieldDescription">Measured against the media\'s actual height, not its filename. Anything taller is served as a transcode capped here, and a request for the original is refused. "No limit" leaves playback untouched.</div>' +
                    '</div>' +
                    '<div class="inputContainer qg-policy-intro-field">' +
                        '<label class="inputLabel inputLabelUnfocused" for="' + introId + '">Custom Intro Video</label>' +
                        '<input type="text" id="' + introId + '" class="emby-input policy-intro" ' +
                            'value="' + escapeAttribute(policy.IntroVideoPath || '') + '" ' +
                            'placeholder="/media/intros/policy-intro.mp4" />' +
                        '<div class="fieldDescription">Optional. Users under this policy see this intro instead of the default.</div>' +
                    '</div>' +
                    '<div class="selectContainer qg-policy-fallback-field">' +
                        '<label class="selectLabel" for="policy-fallback-' + index + '">If No Match Found</label>' +
                        '<select is="emby-select" id="policy-fallback-' + index + '" class="emby-select policy-fallback-mode">' +
                            buildFallbackOptions(policy) +
                        '</select>' +
                        '<div class="fieldDescription">When no file matches the allowed patterns, transcode at the selected resolution instead of blocking.</div>' +
                    '</div>' +
                    '<div class="inputContainer qg-policy-bitrate-field">' +
                        '<label class="inputLabel inputLabelUnfocused" for="policy-bitrate-' + index + '">Max Bitrate (kbps)</label>' +
                        '<input type="number" id="policy-bitrate-' + index + '" class="emby-input policy-fallback-bitrate" ' +
                            'value="' + (policy.FallbackMaxBitrateKbps || 0) + '" min="0" step="1" />' +
                        '<div class="fieldDescription">Override transcode bitrate in kbps (e.g. 4000 for 4 Mbps). 0 = auto from resolution.</div>' +
                    '</div>' +
                    '<div class="checkboxContainer checkboxContainer-withDescription qg-policy-toggle">' +
                        '<label>' +
                            '<input is="emby-checkbox" type="checkbox" class="policy-enabled" id="' + enabledId + '" ' +
                                (policy.Enabled !== false ? 'checked' : '') + ' />' +
                            '<span>Enabled</span>' +
                        '</label>' +
                        '<div class="fieldDescription">Disable this policy without deleting its rules.</div>' +
                    '</div>' +
                    '<div class="qg-policy-actions">' +
                        '<button is="emby-button" type="button" class="raised qg-delete-btn btnDeletePolicy qg-policy-delete" ' +
                            'style="background:#c62828 !important;color:#fff !important;border-color:#c62828 !important;" ' +
                            'data-index="' + index + '">' +
                            '<span>Delete Policy</span>' +
                        '</button>' +
                    '</div>' +
                '</div>' +
            '</div>';
        container.appendChild(card);
    });

    container.querySelectorAll('.btnDeletePolicy').forEach(function (button) {
        button.addEventListener('click', function () {
            deletePolicy(view, parseInt(this.dataset.index, 10));
        });
    });

    container.querySelectorAll('.btnAddPath').forEach(function (button) {
        button.addEventListener('click', function () {
            addPath(view, parseInt(this.dataset.index, 10), this.dataset.list);
        });
    });

    container.querySelectorAll('.btnRemovePath').forEach(function (button) {
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
            'INVALID DEFAULT - currently Full Access' +
            '</option>';
    }

    select.innerHTML += '<option value=""' + (!current ? ' selected' : '') + '>(No default - Full Access)</option>';

    config.Policies.forEach(function (policy) {
        var option;

        if (policy.Enabled === false) {
            return;
        }

        option = document.createElement('option');
        option.value = policy.Id;
        option.textContent = policy.Name || 'Unnamed Policy';
        if (current === policy.Id) {
            option.selected = true;
        }
        select.appendChild(option);
    });
}

function renderApiKeyPolicyDropdown(view) {
    var select = view.querySelector('#apiKeyPolicySelect');
    var current = config.ApiKeyPolicyId;
    var matched = false;

    if (!select) {
        return;
    }

    select.innerHTML = '';
    select.innerHTML += '<option value=""' + (!current ? ' selected' : '') + '>(Uncapped)</option>';

    config.Policies.forEach(function (policy) {
        var option;

        if (policy.Enabled === false) {
            return;
        }

        option = document.createElement('option');
        option.value = policy.Id;
        option.textContent = policy.Name || 'Unnamed Policy';
        if (current === policy.Id) {
            option.selected = true;
            matched = true;
        }
        select.appendChild(option);
    });

    // A configured id that no longer resolves leaves API keys uncapped, so say so rather than
    // silently selecting "(Uncapped)" and letting a save quietly discard the setting.
    if (current && !matched) {
        select.innerHTML += '<option value="' + escapeAttribute(current) + '" selected>' +
            'INVALID POLICY - currently uncapped' +
            '</option>';
    }
}

function renderUserAccess(view) {
    var container = view.querySelector('#userAccessContainer');
    var defaultLabel;
    var pageCount;
    var sortedUsers;
    var visibleUsers;
    var startIndex;
    var endIndex;

    if (users.length === 0) {
        container.innerHTML = getEmptyState('No users found.');
        return;
    }

    clampUserAccessPage();
    pageCount = getUserAccessPageCount();
    sortedUsers = getSortedUsers();
    startIndex = userAccessPage * userAccessPageSize;
    endIndex = Math.min(startIndex + userAccessPageSize, sortedUsers.length);
    visibleUsers = sortedUsers.slice(startIndex, endIndex);
    defaultLabel = escapeHtml(getDefaultPolicyLabel());
    container.innerHTML = '<div class="qg-user-access-table-wrap">' +
        '<table class="qg-user-access-table">' +
            '<thead>' +
                '<tr>' +
                    '<th scope="col" class="qg-user-access-sortable' + (userAccessSortColumn === 'name' ? ' is-active' : '') + '" ' +
                        'data-sort-column="name" tabindex="0" aria-sort="' + (userAccessSortColumn === 'name' ? (userAccessSortDirection === 'asc' ? 'ascending' : 'descending') : 'none') + '">' +
                        '<div class="qg-user-access-sort-content">' +
                            '<span>User</span>' +
                            '<span class="qg-user-access-sort-icon" aria-hidden="true">' + getSortIndicator('name') + '</span>' +
                        '</div>' +
                    '</th>' +
                    '<th scope="col" class="qg-user-access-sortable' + (userAccessSortColumn === 'policy' ? ' is-active' : '') + '" ' +
                        'data-sort-column="policy" tabindex="0" aria-sort="' + (userAccessSortColumn === 'policy' ? (userAccessSortDirection === 'asc' ? 'ascending' : 'descending') : 'none') + '">' +
                        '<div class="qg-user-access-sort-content">' +
                            '<span>Assigned Policy</span>' +
                            '<span class="qg-user-access-sort-icon" aria-hidden="true">' + getSortIndicator('policy') + '</span>' +
                        '</div>' +
                    '</th>' +
                    '<th scope="col" class="qg-user-access-sortable' + (userAccessSortColumn === 'effective' ? ' is-active' : '') + '" ' +
                        'data-sort-column="effective" tabindex="0" aria-sort="' + (userAccessSortColumn === 'effective' ? (userAccessSortDirection === 'asc' ? 'ascending' : 'descending') : 'none') + '">' +
                        '<div class="qg-user-access-sort-content">' +
                            '<span>Effective Access</span>' +
                            '<span class="qg-user-access-sort-icon" aria-hidden="true">' + getSortIndicator('effective') + '</span>' +
                        '</div>' +
                    '</th>' +
                '</tr>' +
            '</thead>' +
            '<tbody>' +
            visibleUsers.map(function (user) {
        var override = getUserOverride(user.Id);
        var overrideValue = override ? override.PolicyId : '';
        var stale = Boolean(overrideValue) && !isValidOverride(overrideValue);
        var effective = getEffectivePolicy(user.Id);
        var effectiveClass = getEffectiveClass(effective);
        var rowClass = stale ? 'qg-user-access-row-invalid' : '';
        var selectId = 'user-policy-' + user.Id;
        var metaText = stale
            ? 'Invalid saved override: this user is currently fail-closed until you choose a valid policy.'
            : '';
        var html =
            '<tr class="' + rowClass + '">' +
                '<td>' +
                    '<div class="qg-user-access-main">' +
                    '<div class="qg-user-access-name">' + escapeHtml(user.Name) + '</div>' +
                    (metaText
                        ? '<div class="qg-user-access-meta">' + escapeHtml(metaText) + '</div>'
                        : '') +
                    '</div>' +
                '</td>' +
                '<td>' +
                    '<div class="qg-user-access-select">' +
                    '<select is="emby-select" id="' + escapeAttribute(selectId) + '" class="user-policy-select" ' +
                        'aria-label="Assigned policy for ' + escapeAttribute(user.Name) + '" ' +
                        'data-userid="' + escapeAttribute(user.Id) + '" ' +
                        'data-username="' + escapeAttribute(user.Name) + '">' ;

        if (stale) {
            html += '<option value="' + escapeAttribute(overrideValue) + '" selected>' +
                'Denied - invalid policy (change this)' +
                '</option>';
        }

        html += '<option value=""' + (!overrideValue ? ' selected' : '') + '>' +
                'Use Default (' + defaultLabel + ')' +
            '</option>' +
            '<option value="' + FULL_ACCESS_POLICY_ID + '"' +
                (overrideValue === FULL_ACCESS_POLICY_ID ? ' selected' : '') + '>' +
                'Full Access' +
            '</option>';

        config.Policies.forEach(function (policy) {
            if (policy.Enabled === false) {
                return;
            }

            html += '<option value="' + escapeAttribute(policy.Id) + '"' +
                (overrideValue === policy.Id ? ' selected' : '') + '>' +
                escapeHtml(policy.Name || 'Unnamed Policy') +
                '</option>';
        });

        html += '</select>' +
                    '</div>' +
                '</td>' +
                '<td>' +
                    '<div class="qg-user-access-effective">' +
                    '<span class="qg-effective ' + effectiveClass + '">' +
                        escapeHtml(effective.name) +
                    '</span>' +
                    '</div>' +
                '</td>' +
            '</tr>';

        return html;
    }).join('') +
            '</tbody>' +
            '<tfoot>' +
                '<tr>' +
                    '<td colspan="3" class="qg-user-access-footerbar-cell">' +
                        '<div class="qg-user-access-footerbar">' +
                            '<div class="qg-user-access-footer-left">' +
                                '<label class="qg-user-access-footer-label" for="userAccessPageSize">Rows per page</label>' +
                                '<select is="emby-select" id="userAccessPageSize" class="user-access-page-size">' +
                                    '<option value="10"' + (userAccessPageSize === 10 ? ' selected' : '') + '>10</option>' +
                                    '<option value="25"' + (userAccessPageSize === 25 ? ' selected' : '') + '>25</option>' +
                                    '<option value="50"' + (userAccessPageSize === 50 ? ' selected' : '') + '>50</option>' +
                                    '<option value="100"' + (userAccessPageSize === 100 ? ' selected' : '') + '>100</option>' +
                                '</select>' +
                            '</div>' +
                            '<div class="qg-user-access-footer-spacer"></div>' +
                            '<div class="qg-user-access-footer-right">' +
                                '<div class="qg-user-access-footer-range">' + (startIndex + 1) + '-' + endIndex + ' of ' + users.length + '</div>' +
                                '<div class="qg-user-access-page-nav">' +
                                    '<button is="emby-button" type="button" class="raised qg-user-access-pager-btn qg-user-access-pager-icon" id="btnUserAccessFirst"' +
                                        (userAccessPage === 0 ? ' disabled' : '') + ' aria-label="First page">' +
                                        '<span aria-hidden="true">&laquo;</span>' +
                                    '</button>' +
                                    '<button is="emby-button" type="button" class="raised qg-user-access-pager-btn qg-user-access-pager-icon" id="btnUserAccessPrev"' +
                                        (userAccessPage === 0 ? ' disabled' : '') + ' aria-label="Previous page">' +
                                        '<span aria-hidden="true">&lsaquo;</span>' +
                                    '</button>' +
                                    '<button is="emby-button" type="button" class="raised qg-user-access-pager-btn qg-user-access-pager-icon" id="btnUserAccessNext"' +
                                        (userAccessPage >= pageCount - 1 ? ' disabled' : '') + ' aria-label="Next page">' +
                                        '<span aria-hidden="true">&rsaquo;</span>' +
                                    '</button>' +
                                    '<button is="emby-button" type="button" class="raised qg-user-access-pager-btn qg-user-access-pager-icon" id="btnUserAccessLast"' +
                                        (userAccessPage >= pageCount - 1 ? ' disabled' : '') + ' aria-label="Last page">' +
                                        '<span aria-hidden="true">&raquo;</span>' +
                                    '</button>' +
                                '</div>' +
                            '</div>' +
                        '</div>' +
                    '</td>' +
                '</tr>' +
            '</tfoot>' +
        '</table>' +
    '</div>';

    container.querySelectorAll('.qg-user-access-sortable').forEach(function (header) {
        header.addEventListener('click', function () {
            setUserAccessSort(this.dataset.sortColumn);
            renderUserAccess(view);
            upgradeNativeWidgets(view);
        });

        header.addEventListener('keydown', function (event) {
            if (event.key === 'Enter' || event.key === ' ') {
                event.preventDefault();
                setUserAccessSort(this.dataset.sortColumn);
                renderUserAccess(view);
                upgradeNativeWidgets(view);
            }
        });
    });

    container.querySelector('#userAccessPageSize').addEventListener('change', function () {
        userAccessPageSize = parseInt(this.value, 10) || 10;
        userAccessPage = 0;
        renderUserAccess(view);
        upgradeNativeWidgets(view);
    });

    container.querySelector('#btnUserAccessFirst').addEventListener('click', function () {
        if (userAccessPage > 0) {
            userAccessPage = 0;
            renderUserAccess(view);
            upgradeNativeWidgets(view);
        }
    });

    container.querySelector('#btnUserAccessPrev').addEventListener('click', function () {
        if (userAccessPage > 0) {
            userAccessPage -= 1;
            renderUserAccess(view);
            upgradeNativeWidgets(view);
        }
    });

    container.querySelector('#btnUserAccessNext').addEventListener('click', function () {
        if (userAccessPage < pageCount - 1) {
            userAccessPage += 1;
            renderUserAccess(view);
            upgradeNativeWidgets(view);
        }
    });

    container.querySelector('#btnUserAccessLast').addEventListener('click', function () {
        if (userAccessPage < pageCount - 1) {
            userAccessPage = pageCount - 1;
            renderUserAccess(view);
            upgradeNativeWidgets(view);
        }
    });
}

function collectFromDOM(view) {
    view.querySelectorAll('#policiesContainer .qg-policy-card').forEach(function (card, index) {
        if (!config.Policies[index]) {
            return;
        }

        config.Policies[index].Name = card.querySelector('.policy-name').value.trim();
        config.Policies[index].AllowedFilenamePatterns = Array.prototype.map.call(
            card.querySelectorAll('.policy-fn-allowed'),
            function (input) {
                return input.value.trim();
            }
        ).filter(Boolean);
        config.Policies[index].BlockedFilenamePatterns = Array.prototype.map.call(
            card.querySelectorAll('.policy-fn-blocked'),
            function (input) {
                return input.value.trim();
            }
        ).filter(Boolean);
        config.Policies[index].IntroVideoPath = card.querySelector('.policy-intro').value.trim();
        config.Policies[index].MaxHeight = toHeight(card.querySelector('.policy-max-height').value);
        var fallbackVal = card.querySelector('.policy-fallback-mode').value;
        config.Policies[index].FallbackTranscode = fallbackVal !== 'off';
        config.Policies[index].FallbackMaxHeight = fallbackVal !== 'off' ? toHeight(fallbackVal) : 0;
        config.Policies[index].FallbackMaxBitrateKbps = parseInt(card.querySelector('.policy-fallback-bitrate').value, 10) || 0;
        config.Policies[index].Enabled = card.querySelector('.policy-enabled').checked;
    });

    config.DefaultPolicyId = view.querySelector('#defaultPolicySelect').value;
    config.ApiKeyPolicyId = view.querySelector('#apiKeyPolicySelect').value;
    config.DefaultIntroVideoPath = view.querySelector('#defaultIntroPath').value.trim();
    config.EnableVersionGrouping = view.querySelector('#enableVersionGrouping').checked;
    config.VersionGroupingSuffixes = splitLines(view.querySelector('#versionGroupingSuffixes').value);
    config.VersionGroupingRoots = splitLines(view.querySelector('#versionGroupingRoots').value);

}

function splitLines(value) {
    return (value || '').split('\n').filter(function (line) {
        return line.trim().length > 0;
    });
}

function refreshComputedPreview(view) {
    if (!isLoaded) {
        return;
    }

    collectFromDOM(view);
    renderDefaultPolicyDropdown(view);
    renderApiKeyPolicyDropdown(view);
    renderUserAccess(view);
    upgradeNativeWidgets(view);
}

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
        AllowedFilenamePatterns: [],
        BlockedFilenamePatterns: [],
        Enabled: true,
        MaxHeight: 0,
        FallbackTranscode: false,
        FallbackMaxHeight: 0,
        FallbackMaxBitrateKbps: 0,
        BlockedMessageHeader: 'Quality Restricted',
        BlockedMessageText: 'This quality version is not available for your account.',
        BlockedMessageTimeoutMs: 8000,
        IntroVideoPath: ''
    });

    renderAll(view);
    markDirty(view);
    focusElement(view, '.qg-policy-card[data-index="' + newIndex + '"] .policy-name');
}

function deletePolicy(view, index) {
    var deletedId;

    if (!isLoaded || !config.Policies[index]) {
        return;
    }

    if (!confirm('Delete policy "' + (config.Policies[index].Name || 'Unnamed Policy') + '"?')) {
        return;
    }

    collectFromDOM(view);
    deletedId = config.Policies[index].Id;
    config.Policies.splice(index, 1);

    renderAll(view);
    markDirty(view);
}

function addPath(view, policyIndex, listName) {
    var card = view.querySelector('.qg-policy-card[data-index="' + policyIndex + '"]');
    var key;
    var paths;

    if (!isLoaded) {
        return;
    }

    collectFromDOM(view);
    if (!config.Policies[policyIndex]) {
        return;
    }

    key = getFieldKey(listName);
    paths = config.Policies[policyIndex][key] || [];
    padPathRows(card, listName, paths);
    paths.push('');
    config.Policies[policyIndex][key] = paths;

    renderAll(view);
    markDirty(view);

    var inputs = view.querySelectorAll('.qg-policy-card[data-index="' + policyIndex + '"] .policy-' + listName);
    if (inputs.length) {
        inputs[inputs.length - 1].focus();
    }
}

function removePath(view, policyIndex, listName, rowIndex) {
    var card = view.querySelector('.qg-policy-card[data-index="' + policyIndex + '"]');
    var key;
    var paths;

    if (!isLoaded) {
        return;
    }

    collectFromDOM(view);
    if (!config.Policies[policyIndex]) {
        return;
    }

    key = getFieldKey(listName);
    paths = config.Policies[policyIndex][key] || [];
    padPathRows(card, listName, paths);

    if (rowIndex >= 0 && rowIndex < paths.length) {
        paths.splice(rowIndex, 1);
    }

    config.Policies[policyIndex][key] = paths;
    renderAll(view);
    markDirty(view);

    var remainingInputs = view.querySelectorAll('.qg-policy-card[data-index="' + policyIndex + '"] .policy-' + listName);
    if (remainingInputs.length) {
        remainingInputs[Math.max(0, rowIndex - 1)].focus();
    }
}

function loadConfig(view) {
    isLoaded = false;
    userAccessPage = 0;
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
        config.ApiKeyPolicyId = config.ApiKeyPolicyId || '';
        config.EnableVersionGrouping = config.EnableVersionGrouping || false;
        config.VersionGroupingRoots = config.VersionGroupingRoots || [];
        // Empty means the default, which the textarea shows as its placeholder. Filling it in
        // here would write the default back into the saved list on the next save.
        config.VersionGroupingSuffixes = config.VersionGroupingSuffixes || [];
        // Ensure fields exist on each policy (upgrade from older versions)
        config.Policies.forEach(function (policy) {
            policy.AllowedFilenamePatterns = policy.AllowedFilenamePatterns || [];
            policy.BlockedFilenamePatterns = policy.BlockedFilenamePatterns || [];
            policy.MaxHeight = policy.MaxHeight || 0;
            policy.FallbackTranscode = policy.FallbackTranscode || false;
            policy.FallbackMaxHeight = policy.FallbackMaxHeight || 0;
            policy.FallbackMaxBitrateKbps = policy.FallbackMaxBitrateKbps || 0;
        });
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
        var message = err instanceof Response
            ? 'HTTP ' + err.status + ' ' + err.statusText
            : (err && err.message ? err.message : String(err));
        console.error('QualityGate: failed to load configuration:', err);
        setLoadStatus(view, 'Error: ' + message, 'error');
        setSaveStatus(view, 'Unable to load configuration.', 'error');
    });
}

function validateRegexPatterns() {
    var errors = [];

    (config.Policies || []).forEach(function (policy, policyIndex) {
        var allPatterns = (policy.AllowedFilenamePatterns || []).concat(policy.BlockedFilenamePatterns || []);
        allPatterns.forEach(function (pattern) {
            try {
                new RegExp(pattern, 'i');
            } catch (e) {
                errors.push('Policy "' + (policy.Name || 'Policy ' + (policyIndex + 1)) + '": invalid regex "' + pattern + '" — ' + e.message);
            }
        });
    });

    return errors;
}

function saveConfig(view) {
    if (!isLoaded) {
        return;
    }

    collectFromDOM(view);

    var regexErrors = validateRegexPatterns();
    if (regexErrors.length > 0) {
        setSaveStatus(view, 'Invalid regex patterns detected.', 'error');
        Dashboard.alert('Fix these regex patterns before saving:\n\n' + regexErrors.join('\n'));
        return;
    }

    setSaveStatus(view, 'Saving...', 'warning');

    ApiClient.updatePluginConfiguration(PLUGIN_ID, config).then(function () {
        setSaveStatus(view, 'Saved.', 'success');
        Dashboard.processPluginConfigurationUpdateResult();
    }).catch(function (err) {
        var message = err instanceof Response
            ? 'HTTP ' + err.status + ' ' + err.statusText
            : (err && err.message ? err.message : String(err));
        console.error('QualityGate: failed to save configuration:', err);
        setSaveStatus(view, 'Error saving: ' + message, 'error');
        Dashboard.alert('Error saving: ' + message);
    });
}

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
        if (!isLoaded) {
            return;
        }

        markDirty(view);
    });

    form.addEventListener('change', function (event) {
        if (!isLoaded) {
            return;
        }

        if (event.target.matches('.user-access-page-size')) {
            return;
        }

        if (event.target.matches('.user-policy-select')) {
            setUserPolicyAssignment(
                event.target.dataset.userid,
                event.target.dataset.username,
                event.target.value
            );
            renderUserAccess(view);
            upgradeNativeWidgets(view);
            markDirty(view);
            return;
        }

        if (event.target.matches('#defaultPolicySelect, #apiKeyPolicySelect, .policy-enabled, .policy-fallback-transcode, .policy-name')) {
            refreshComputedPreview(view);
        }

        markDirty(view);
    });

    view.addEventListener('viewshow', function () {
        loadConfig(view);
    });
}
