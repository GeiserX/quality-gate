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
    if (view.querySelector('#encodeTargetsContainer')) {
        view.querySelector('#encodeTargetsContainer').innerHTML = '';
    }
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

/** Builds the per-policy toggle that keeps within-cap versions out of bitrate-only transcodes. */
export function buildKeepDirectToggle(policy, index) {
    var id = 'policy-keep-direct-' + index;

    return '<div class="checkboxContainer checkboxContainer-withDescription qg-policy-toggle">' +
        '<label>' +
            '<input is="emby-checkbox" type="checkbox" class="policy-keep-direct" id="' + id + '" ' +
                (policy.KeepWithinCapVersionsDirect ? 'checked' : '') + ' />' +
            '<span>Play within-cap versions as they are</span>' +
        '</label>' +
        '<div class="fieldDescription">A version already within the maximum resolution is no longer transcoded just to meet a client\'s bitrate limit; a codec, audio or subtitle the client cannot play still transcodes it.</div>' +
    '</div>';
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
    renderEncodePriority(view);
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
                    buildKeepDirectToggle(policy, index) +
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
        config.Policies[index].KeepWithinCapVersionsDirect = card.querySelector('.policy-keep-direct').checked;
    });

    config.DefaultPolicyId = view.querySelector('#defaultPolicySelect').value;
    config.ApiKeyPolicyId = view.querySelector('#apiKeyPolicySelect').value;
    config.DefaultIntroVideoPath = view.querySelector('#defaultIntroPath').value.trim();
    config.EnableVersionGrouping = view.querySelector('#enableVersionGrouping').checked;
    config.VersionGroupingSuffixes = splitLines(view.querySelector('#versionGroupingSuffixes').value);
    config.VersionGroupingRoots = splitLines(view.querySelector('#versionGroupingRoots').value);
    collectEncodePriority(view);
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
    renderEncodePriority(view);
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
        KeepWithinCapVersionsDirect: false,
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
            policy.KeepWithinCapVersionsDirect = policy.KeepWithinCapVersionsDirect || false;
        });
        normalizeEncodePriority(config);
        return Promise.all([ApiClient.getUsers(), loadLibraryLocations()]);
    }).then(function (results) {
        var enabledPolicies;
        var userList = results[0];

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
        loadEncodePriorityStatus(view);
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
    dropEmptyFolders(config);

    var regexErrors = validateRegexPatterns();
    if (regexErrors.length > 0) {
        setSaveStatus(view, 'Invalid regex patterns detected.', 'error');
        Dashboard.alert('Fix these regex patterns before saving:\n\n' + regexErrors.join('\n'));
        return;
    }

    var encodeErrors = validateEncodeTargets(config, libraryLocations);
    if (encodeErrors.length > 0) {
        setSaveStatus(view, 'Encode priority settings need fixing.', 'error');
        Dashboard.alert('Fix these encode priority settings before saving:\n\n' + encodeErrors.join('\n'));
        return;
    }

    setSaveStatus(view, 'Saving...', 'warning');

    ApiClient.updatePluginConfiguration(PLUGIN_ID, config).then(function () {
        setSaveStatus(view, 'Saved.', 'success');
        Dashboard.processPluginConfigurationUpdateResult();
        setTimeout(function () {
            loadEncodePriorityStatus(view);
        }, 3000);
    }).catch(function (err) {
        var message = err instanceof Response
            ? 'HTTP ' + err.status + ' ' + err.statusText
            : (err && err.message ? err.message : String(err));
        console.error('QualityGate: failed to save configuration:', err);
        setSaveStatus(view, 'Error saving: ' + message, 'error');
        Dashboard.alert('Error saving: ' + message);
    });
}

/* ---------------------------------------------------------------------------------------------
 * Encode priority
 *
 * Everything below renders the Encode Priority section and maps it back into the config object.
 * The page posts the whole object, so fields this section does not render still round-trip. The
 * pure helpers are exported so the node-driven tests exercise exactly what the browser runs.
 * ------------------------------------------------------------------------------------------- */

var ENCODE_PRIORITY_TASK_KEY = 'QualityGateEncodePriority';
var ENCODE_PRIORITY_ACTIVITY_TYPE = 'QualityGate.EncodePriority';
var SOURCE_FOLDER_FILE = '.encoder-priority.json';
var DATA_FOLDER_PLACEHOLDER = '<Jellyfin data folder>';
var OUTPUT_HEIGHT_PRESETS = [480, 576, 720, 1080, 1440, 2160];
var SAMPLE_FILE = 'Show Name/Season 2/Show Name S02E05.mkv';
var libraryLocations = [];
var epCardState = {};
var epDataPath = '';

function orDefault(value, fallback) {
    return value === undefined || value === null ? fallback : value;
}

function toInt(value, fallback) {
    var parsed = parseInt(value, 10);
    return isFinite(parsed) ? parsed : fallback;
}

/**
 * A stored mode as the server reads it: trimmed, case ignored, an unknown value the default.
 * The page compares modes exactly, so a hand-edited "datafolder" must become "DataFolder" here.
 */
function canonicalMode(value, names, fallback) {
    var wanted = String(value === undefined || value === null ? '' : value).trim().toLowerCase();
    for (var i = 0; i < names.length; i++) {
        if (names[i].toLowerCase() === wanted) {
            return names[i];
        }
    }

    return fallback;
}

/** Fills in every encode priority field a config from an older release or a hand edit lacks. */
export function normalizeEncodePriority(cfg) {
    cfg.EnableEncodePriority = Boolean(cfg.EnableEncodePriority);
    cfg.PriorityNowPlaying = orDefault(cfg.PriorityNowPlaying, true);
    cfg.PriorityContinueWatching = orDefault(cfg.PriorityContinueWatching, true);
    cfg.PriorityContinueWatchingPerUser = orDefault(cfg.PriorityContinueWatchingPerUser, 20);
    cfg.PriorityNextUp = orDefault(cfg.PriorityNextUp, true);
    cfg.PriorityNextUpDepth = orDefault(cfg.PriorityNextUpDepth, 3);
    cfg.PriorityNextUpShowsPerUser = orDefault(cfg.PriorityNextUpShowsPerUser, 10);
    cfg.PriorityNextUpIncludeSpecials = orDefault(cfg.PriorityNextUpIncludeSpecials, false);
    cfg.PriorityFavourites = orDefault(cfg.PriorityFavourites, false);
    cfg.PriorityFavouritesPerUser = orDefault(cfg.PriorityFavouritesPerUser, 25);
    cfg.PriorityWatchedWithinDays = orDefault(cfg.PriorityWatchedWithinDays, 30);
    cfg.PriorityUnprobedNeedsEncode = orDefault(cfg.PriorityUnprobedNeedsEncode, false);
    cfg.PriorityRefreshOnPlayback = orDefault(cfg.PriorityRefreshOnPlayback, true);
    cfg.PriorityDebounceMinutes = orDefault(cfg.PriorityDebounceMinutes, 10);
    cfg.PriorityRunBudgetSeconds = orDefault(cfg.PriorityRunBudgetSeconds, 180);
    cfg.EncodeTargets = (cfg.EncodeTargets || []).map(normalizeEncodeTarget);
    return cfg;
}

/** Fills in one target's missing fields. Stored ids that no longer resolve are kept as they are. */
export function normalizeEncodeTarget(target) {
    var t = target || {};
    t.Id = t.Id || generateId();
    t.Name = orDefault(t.Name, '');
    t.Enabled = orDefault(t.Enabled, true);
    t.Folders = (t.Folders || []).map(function (folder) {
        return { JellyfinPath: (folder && folder.JellyfinPath) || '', EncoderPath: (folder && folder.EncoderPath) || '' };
    });
    // Read the way the server reads them, so the page shows and saves what the server uses.
    t.OutputHeight = Math.min(Math.max(toInt(orDefault(t.OutputHeight, 720), 720), 144), 4320);
    t.OutputMode = canonicalMode(t.OutputMode, ['SourceFolder', 'DataFolder', 'Custom'], 'SourceFolder');
    t.OutputPath = t.OutputPath || '';
    t.AudienceMode = canonicalMode(t.AudienceMode, ['Auto', 'Policies', 'Users'], 'Auto');
    t.AudiencePolicyIds = (t.AudiencePolicyIds || []).slice();
    t.AudienceUserIds = (t.AudienceUserIds || []).slice();
    t.ExcludedUserIds = (t.ExcludedUserIds || []).slice();
    t.ResolveSymlinks = Boolean(t.ResolveSymlinks);
    t.MaxEntries = orDefault(t.MaxEntries, 300);
    t.DryRun = Boolean(t.DryRun);
    return t;
}

function normalizeRoot(path, isWindows) {
    var value = String(path || '');
    return isWindows
        ? value.replace(/\//g, '\\').replace(/\\+$/, '')
        : value.replace(/\/+$/, '');
}

/**
 * The part of a path below a folder, joined with "/", or null when the path is not inside it.
 * The same rule as the server's PathRoots: equal to the folder or folder plus a separator, both
 * separators and case-insensitive on Windows only.
 */
export function relativeTo(path, root, isWindows) {
    var subject = normalizeRoot(path, isWindows);
    var folder = normalizeRoot(root, isWindows);
    var separator = isWindows ? '\\' : '/';
    var a;
    var b;

    if (!subject || !folder) {
        return null;
    }

    a = isWindows ? subject.toUpperCase() : subject;
    b = isWindows ? folder.toUpperCase() : folder;
    if (a === b) {
        return '';
    }

    if (a.length > b.length + 1 && a.charAt(b.length) === separator && a.substring(0, b.length) === b) {
        return isWindows ? subject.substring(folder.length + 1).replace(/\\/g, '/') : subject.substring(folder.length + 1);
    }

    return null;
}

function cleanEncoderPath(path) {
    return String(path || '').trim().split('/').filter(function (part) {
        return part.length > 0 && part !== '.';
    }).join('/');
}

/** Maps a path Jellyfin sees onto the encoder's source folder, through the most specific folder. */
export function mapToEncoderPath(path, mappings, isWindows) {
    var best = null;
    var bestLength = -1;
    var remainder = '';

    (mappings || []).forEach(function (mapping) {
        var rest = relativeTo(path, mapping.JellyfinPath, isWindows);
        var length;
        if (rest === null) {
            return;
        }

        length = normalizeRoot(mapping.JellyfinPath, isWindows).length;
        if (length > bestLength) {
            best = mapping;
            bestLength = length;
            remainder = rest;
        }
    });

    if (!best || remainder === '') {
        return null;
    }

    var prefix = cleanEncoderPath(best.EncoderPath);
    return prefix ? prefix + '/' + remainder : remainder;
}

function joinPath(folder, name) {
    var trimmed = String(folder || '').replace(/[\\/]+$/, '');
    var separator = trimmed.indexOf('\\') !== -1 && trimmed.indexOf('/') === -1 ? '\\' : '/';
    return trimmed + separator + name;
}

function dataFolderStem(id) {
    return String(id || '').substring(0, 8).replace(/[^A-Za-z0-9_-]/g, '_');
}

/**
 * The file a target writes, as Jellyfin sees it, or why it has none. dataPath is the server's
 * data folder when known; otherwise a placeholder stands in for it.
 */
export function resolveOutputPath(target, dataPath) {
    var mode = target.OutputMode || 'SourceFolder';
    var source;

    if (mode === 'DataFolder') {
        return { path: joinPath(joinPath(joinPath(dataPath || DATA_FOLDER_PLACEHOLDER, 'quality-gate'), 'encode-priority'), dataFolderStem(target.Id) + '.json'), error: '' };
    }

    if (mode === 'Custom') {
        if (!isAbsolutePath(target.OutputPath)) {
            return { path: '', error: 'A custom output needs an absolute file path.' };
        }

        return { path: target.OutputPath, error: '' };
    }

    source = (target.Folders || []).find(function (folder) {
        return cleanEncoderPath(folder.EncoderPath) === '' && folder.JellyfinPath;
    });
    if (!source) {
        return { path: '', error: 'The encoder\'s source folder is not a folder Jellyfin sees; use Data folder or Custom.' };
    }

    return { path: joinPath(source.JellyfinPath, SOURCE_FOLDER_FILE), error: '' };
}

function isAbsolutePath(path) {
    var value = String(path || '').trim();
    return value.charAt(0) === '/' || /^[A-Za-z]:[\\/]/.test(value) || value.substring(0, 2) === '\\\\';
}

function findPolicy(cfg, policyId) {
    return (cfg.Policies || []).find(function (policy) {
        return policy.Id === policyId;
    });
}

/**
 * The policy a user plays under, resolved like the server does: an override, else the default.
 * A missing or disabled policy is the deny-all sentinel, which playback treats as uncapped.
 */
function effectivePolicyOf(cfg, userId) {
    var override = (cfg.UserPolicies || []).find(function (assignment) {
        return assignment.UserId === userId;
    });
    var policyId = override && override.PolicyId ? override.PolicyId : cfg.DefaultPolicyId;
    var policy;

    if (override && override.PolicyId === FULL_ACCESS_POLICY_ID) {
        return { policy: null, denyAll: false };
    }

    if (!policyId) {
        return { policy: null, denyAll: false };
    }

    policy = findPolicy(cfg, policyId);
    if (!policy || policy.Enabled === false) {
        return { policy: null, denyAll: true };
    }

    return { policy: policy, denyAll: false };
}

/**
 * Who a target serves, for the derived lines on its card. Counts users per policy; the server
 * additionally skips viewers idle for longer than the activity window.
 */
export function targetAudience(target, cfg, userList) {
    var height = toInt(target.OutputHeight, 720);
    var excluded = target.ExcludedUserIds || [];
    var groups = {};
    var order = [];
    var viewers = 0;
    var belowOutput = 0;

    (userList || []).forEach(function (user) {
        var resolved;
        var cap;
        var key;
        var label;

        if ((user.Policy && user.Policy.IsDisabled) || excluded.indexOf(user.Id) !== -1) {
            return;
        }

        resolved = effectivePolicyOf(cfg, user.Id);
        if (resolved.denyAll) {
            return;
        }

        cap = resolved.policy ? toInt(resolved.policy.MaxHeight, 0) : 0;
        if (target.AudienceMode === 'Users') {
            if ((target.AudienceUserIds || []).indexOf(user.Id) === -1) {
                return;
            }

            if (cap > 0 && cap < height) {
                belowOutput += 1;
                return;
            }

            key = resolved.policy ? resolved.policy.Id : '__uncapped__';
            label = resolved.policy ? (resolved.policy.Name || 'Unnamed Policy') : 'Uncapped users';
            cap = cap > 0 ? cap : height;
        } else {
            if (!(cap > 0 && cap >= height)) {
                return;
            }

            if (target.AudienceMode === 'Policies' && (target.AudiencePolicyIds || []).indexOf(resolved.policy.Id) === -1) {
                return;
            }

            key = resolved.policy.Id;
            label = resolved.policy.Name || 'Unnamed Policy';
        }

        if (!groups[key]) {
            groups[key] = { name: label, cap: cap, viewers: 0 };
            order.push(key);
        }

        groups[key].viewers += 1;
        viewers += 1;
    });

    return {
        viewers: viewers,
        belowOutput: belowOutput,
        serves: order.map(function (key) {
            return groups[key];
        }),
        cannotHelp: (cfg.Policies || []).filter(function (policy) {
            var cap = toInt(policy.MaxHeight, 0);
            return policy.Enabled !== false && cap > 0 && cap < height;
        }).map(function (policy) {
            return { name: policy.Name || 'Unnamed Policy', cap: toInt(policy.MaxHeight, 0) };
        })
    };
}

/**
 * Checks the encode priority settings before a save. Returns one line per problem. Disabled
 * encoders are only checked for a name, so a half-configured one can be parked.
 */
export function validateEncodeTargets(cfg, locations) {
    var errors = [];
    var names = {};
    var outputs = {};

    if (!cfg.EnableEncodePriority) {
        return errors;
    }

    (cfg.EncodeTargets || []).forEach(function (target, index) {
        var label = 'Encoder "' + (target.Name || ('#' + (index + 1))) + '"';
        var name = String(target.Name || '').trim();
        var seenFolders = [];
        var output;

        if (!name || name.length > 64) {
            errors.push('Encoder #' + (index + 1) + ': a name of 1 to 64 characters is required.');
        } else if (names[name.toLowerCase()]) {
            errors.push(label + ': another encoder has the same name.');
        } else {
            names[name.toLowerCase()] = true;
        }

        if (target.Enabled === false) {
            return;
        }

        if (!(target.Folders || []).length) {
            errors.push(label + ': add at least one folder.');
        }

        (target.Folders || []).forEach(function (folder) {
            var encoderPath = String(folder.EncoderPath || '').trim();
            if (!isAbsolutePath(folder.JellyfinPath)) {
                errors.push(label + ': "' + (folder.JellyfinPath || '') + '" must be an absolute path, as Jellyfin sees it.');
                return;
            }

            if (encoderPath.charAt(0) === '/' || encoderPath.indexOf('\\') !== -1 || encoderPath.split('/').indexOf('..') !== -1) {
                errors.push(label + ': the encoder path "' + encoderPath + '" must be relative, use / and contain no "..".');
            }

            seenFolders.forEach(function (other) {
                if (relativeTo(folder.JellyfinPath, other, false) !== null || relativeTo(other, folder.JellyfinPath, false) !== null) {
                    errors.push(label + ': the folders "' + other + '" and "' + folder.JellyfinPath + '" overlap.');
                }
            });
            seenFolders.push(folder.JellyfinPath);
        });

        if (target.AudienceMode === 'Policies' && !(target.AudiencePolicyIds || []).length) {
            errors.push(label + ': choose at least one policy, or switch who counts back to capped viewers.');
        }

        if (target.AudienceMode === 'Users' && !(target.AudienceUserIds || []).length) {
            errors.push(label + ': choose at least one user, or switch who counts back to capped viewers.');
        }

        output = resolveOutputPath(target, '');
        if (output.error) {
            errors.push(label + ': ' + output.error);
            return;
        }

        if (target.OutputMode === 'Custom' && (locations || []).some(function (location) {
            return relativeTo(target.OutputPath, location, false) !== null;
        }) && String(target.OutputPath).split(/[\\/]/).pop().charAt(0) !== '.') {
            errors.push(label + ': "' + target.OutputPath + '" is inside a library, so its file name must start with a dot.');
        }

        if (!target.DryRun) {
            if (outputs[output.path]) {
                errors.push(label + ': another encoder already writes ' + output.path + '.');
            }

            outputs[output.path] = true;
        }
    });

    return errors;
}

/** The output height options, with the stored height always among them. */
export function outputHeightOptions(configured) {
    var selected = toInt(configured, 720);
    var choices = OUTPUT_HEIGHT_PRESETS.slice();

    if (selected > 0 && choices.indexOf(selected) === -1) {
        choices.push(selected);
        choices.sort(function (a, b) {
            return a - b;
        });
    }

    return choices.map(function (height) {
        return buildOption(height, height + 'p', height === selected);
    }).join('');
}

/** The copy-paste text for the encoder, per output mode. */
export function encoderSetupText(target, resolvedPath) {
    if (target.OutputMode === 'DataFolder') {
        return 'Jellyfin\'s media mount is often read-only, so the file is written under Jellyfin\'s data folder. ' +
            'Mount ' + resolvedPath + ' into the encoder read-only and set PRIORITY_FILE to its path inside the encoder container. ' +
            'In the official and linuxserver images the data folder is /config/data.';
    }

    if (target.OutputMode === 'Custom') {
        return 'Set PRIORITY_FILE to this file as the encoder sees it.';
    }

    return 'Nothing to set. quality-gate-encoder reads <SOURCE_FOLDER>/' + SOURCE_FOLDER_FILE + ' by default.';
}

/** The policy checklist for the Policies audience, keeping ids that no longer resolve. */
export function buildAudiencePolicyChecklist(target, policies, index) {
    var chosen = target.AudiencePolicyIds || [];
    var height = toInt(target.OutputHeight, 720);
    var html = (policies || []).map(function (policy) {
        var cap = toInt(policy.MaxHeight, 0);
        var greyed = !(cap > 0 && cap >= height);
        return '<label data-greyed="' + greyed + '">' +
            '<input type="checkbox" class="ep-audience-policy" data-index="' + index + '" value="' + escapeAttribute(policy.Id) + '"' +
                (chosen.indexOf(policy.Id) !== -1 ? ' checked' : '') + ' /> ' +
            escapeHtml(policy.Name || 'Unnamed Policy') + ' (' + (cap > 0 ? cap + 'p' : 'no cap') + ')' +
            (greyed ? ' - below this encoder\'s ' + height + 'p output' : '') +
            '</label>';
    }).join('');

    chosen.forEach(function (id) {
        if (!(policies || []).some(function (policy) {
            return policy.Id === id;
        })) {
            html += '<label><input type="checkbox" class="ep-audience-policy" data-index="' + index + '" value="' + escapeAttribute(id) + '" checked /> ' +
                'Deleted policy (' + escapeHtml(String(id).substring(0, 8)) + '…)</label>';
        }
    });

    return html;
}

/** A user checklist, keeping ids that no longer resolve. */
export function buildUserChecklist(chosen, userList, index, className) {
    var ids = chosen || [];
    var html = (userList || []).map(function (user) {
        return '<label><input type="checkbox" class="' + className + '" data-index="' + index + '" value="' + escapeAttribute(user.Id) + '"' +
            (ids.indexOf(user.Id) !== -1 ? ' checked' : '') + ' /> ' + escapeHtml(user.Name) + '</label>';
    }).join('');

    ids.forEach(function (id) {
        if (!(userList || []).some(function (user) {
            return user.Id === id;
        })) {
            html += '<label><input type="checkbox" class="' + className + '" data-index="' + index + '" value="' + escapeAttribute(id) + '" checked /> ' +
                'Unknown user (' + escapeHtml(String(id).substring(0, 8)) + '…)</label>';
        }
    });

    return html || '<span class="fieldDescription">No users.</span>';
}

function targetSummary(target, audience, output) {
    var folders = (target.Folders || []).map(function (folder) {
        var encoder = cleanEncoderPath(folder.EncoderPath);
        return (folder.JellyfinPath || '?') + ' → ' + (encoder || 'encoder root');
    }).join(', ');

    return (target.Name || 'Unnamed encoder') +
        (target.Enabled === false ? ' (off)' : '') +
        ' · ' + (folders || 'no folder') +
        ' · ' + toInt(target.OutputHeight, 720) + 'p' +
        ' · serves ' + audience.serves.length + ' ' + (audience.serves.length === 1 ? 'group' : 'groups') + ' (' + audience.viewers + ' viewers)' +
        ' · ' + (target.DryRun ? 'dry run, writes nothing' : (output.path ? 'writes ' + output.path : output.error));
}

function buildFolderRows(target, index) {
    var rows = target.Folders.length ? target.Folders : [{ JellyfinPath: '', EncoderPath: '' }];
    return rows.map(function (folder, row) {
        return '<div class="ep-folder-row">' +
            '<div class="inputContainer">' +
                '<label class="inputLabel inputLabelUnfocused" for="ep-folder-' + index + '-' + row + '">Jellyfin folder</label>' +
                '<input type="text" class="emby-input ep-jellyfin-path" id="ep-folder-' + index + '-' + row + '" data-index="' + index + '" list="epLibraryLocations" ' +
                    'value="' + escapeAttribute(folder.JellyfinPath) + '" placeholder="/media/tv" />' +
            '</div>' +
            '<div class="inputContainer ep-encoder-col">' +
                '<label class="inputLabel inputLabelUnfocused" for="ep-encoder-' + index + '-' + row + '">Inside the encoder\'s source folder</label>' +
                '<input type="text" class="emby-input ep-encoder-path" id="ep-encoder-' + index + '-' + row + '" data-index="' + index + '" ' +
                    'value="' + escapeAttribute(folder.EncoderPath) + '" placeholder="usually empty" />' +
            '</div>' +
            '<button is="emby-button" type="button" class="raised ep-remove-folder" data-index="' + index + '" data-row="' + row + '" aria-label="Remove folder">' +
                '<span>×</span>' +
            '</button>' +
        '</div>';
    }).join('');
}

function buildTargetCard(target, index) {
    var audience = targetAudience(target, config, users);
    var output = resolveOutputPath(target, epDataPath);
    var example = target.Folders.length
        ? mapToEncoderPath(joinPath(target.Folders[0].JellyfinPath, SAMPLE_FILE), target.Folders, false)
        : null;
    var state = epCardState[target.Id] || { open: !target.Name, advanced: false };
    var modes = [['SourceFolder', 'Beside the media (source folder)'], ['DataFolder', 'Jellyfin data folder'], ['Custom', 'Custom file']];
    var audienceModes = [['Auto', 'Capped viewers this encoder can help'], ['Policies', 'Only users on these policies'], ['Users', 'These users, even if uncapped']];
    var serves = audience.serves.map(function (group) {
        return '<em>' + escapeHtml(group.name) + '</em> (' + group.viewers + (group.viewers === 1 ? ' viewer' : ' viewers') + ')';
    }).join(', ');
    var cannotHelp = audience.cannotHelp.map(function (policy) {
        return '<em>' + escapeHtml(policy.name) + '</em> (' + policy.cap + 'p)';
    }).join(', ');

    return '<details class="ep-card' + (state.advanced ? ' ep-show-advanced' : '') + '" data-index="' + index + '" data-id="' + escapeAttribute(target.Id) + '"' + (state.open ? ' open' : '') + '>' +
        '<summary>' + escapeHtml(targetSummary(target, audience, output)) + '</summary>' +
        '<div class="inputContainer">' +
            '<label class="inputLabel inputLabelUnfocused" for="ep-name-' + index + '">Name</label>' +
            '<input type="text" class="emby-input ep-name" id="ep-name-' + index + '" data-index="' + index + '" maxlength="64" value="' + escapeAttribute(target.Name) + '" placeholder="Shows" />' +
        '</div>' +
        '<label class="checkboxContainer"><input is="emby-checkbox" type="checkbox" class="ep-enabled" data-index="' + index + '"' + (target.Enabled !== false ? ' checked' : '') + ' /><span>Enabled</span></label>' +
        '<h4 class="ep-block-title">Folders</h4>' +
        buildFolderRows(target, index) +
        '<button is="emby-button" type="button" class="raised ep-add-folder" data-index="' + index + '"><span>Add folder</span></button>' +
        '<div class="ep-example">' + (example
            ? '<code>' + escapeHtml(joinPath(target.Folders[0].JellyfinPath, SAMPLE_FILE)) + '</code> will be written as <code>' + escapeHtml(example) + '</code>'
            : 'Add a folder to see how a file is written.') + '</div>' +
        '<div class="selectContainer">' +
            '<label class="selectLabel" for="ep-height-' + index + '">Encoder output height</label>' +
            '<select is="emby-select" class="emby-select ep-output-height" id="ep-height-' + index + '" data-index="' + index + '">' + outputHeightOptions(target.OutputHeight) + '</select>' +
            '<div class="fieldDescription">What the encoder produces. quality-gate-encoder always produces 720p.</div>' +
        '</div>' +
        '<h4 class="ep-block-title">Where to write</h4>' +
        modes.map(function (mode) {
            return '<label class="checkboxContainer"><input type="radio" name="ep-output-mode-' + index + '" class="ep-output-mode" data-index="' + index + '" value="' + mode[0] + '"' +
                (target.OutputMode === mode[0] ? ' checked' : '') + ' /> <span>' + mode[1] + '</span></label>';
        }).join('') +
        (target.OutputMode === 'Custom'
            ? '<div class="inputContainer"><label class="inputLabel inputLabelUnfocused" for="ep-output-path-' + index + '">File, as Jellyfin sees it</label>' +
                '<input type="text" class="emby-input ep-output-path" id="ep-output-path-' + index + '" data-index="' + index + '" value="' + escapeAttribute(target.OutputPath) + '" placeholder="/media/tv/.encoder-priority.json" /></div>'
            : '') +
        '<div class="ep-setup">' +
            (output.error
                ? '<span class="ep-error">' + escapeHtml(output.error) + '</span>'
                : 'Writes <code class="ep-resolved-path">' + escapeHtml(output.path) + '</code><br /><span class="ep-setup-text">' + escapeHtml(encoderSetupText(target, output.path)) + '</span>') +
        '</div>' +
        '<div class="ep-derived">' +
            (audience.serves.length ? 'Serves: ' + serves + '.' : '<span class="ep-warning">Serves nobody yet: no capped viewer has a cap at or above ' + toInt(target.OutputHeight, 720) + 'p.</span>') +
            (audience.cannotHelp.length ? '<br /><span class="ep-warning">Cannot help: ' + cannotHelp + ', whose cap is below this encoder\'s ' + toInt(target.OutputHeight, 720) + 'p output. Those viewers keep getting live transcodes.</span>' : '') +
            (audience.belowOutput ? '<br /><span class="ep-warning">' + audience.belowOutput + ' chosen users are capped below the output and are skipped.</span>' : '') +
        '</div>' +
        '<details class="ep-card-advanced"' + (state.advanced ? ' open' : '') + ' data-index="' + index + '">' +
            '<summary>Advanced</summary>' +
            '<h4 class="ep-block-title">Who counts</h4>' +
            audienceModes.map(function (mode) {
                return '<label class="checkboxContainer"><input type="radio" name="ep-audience-' + index + '" class="ep-audience-mode" data-index="' + index + '" value="' + mode[0] + '"' +
                    (target.AudienceMode === mode[0] ? ' checked' : '') + ' /> <span>' + mode[1] + '</span></label>';
            }).join('') +
            (target.AudienceMode === 'Policies' ? '<div class="ep-checklist">' + buildAudiencePolicyChecklist(target, config.Policies, index) + '</div>' : '') +
            (target.AudienceMode === 'Users' ? '<div class="ep-checklist">' + buildUserChecklist(target.AudienceUserIds, users, index, 'ep-audience-user') + '</div>' : '') +
            '<h4 class="ep-block-title">Never count these users</h4>' +
            '<div class="ep-checklist">' + buildUserChecklist(target.ExcludedUserIds, users, index, 'ep-excluded-user') + '</div>' +
            '<label class="checkboxContainer"><input is="emby-checkbox" type="checkbox" class="ep-resolve-symlinks" data-index="' + index + '"' + (target.ResolveSymlinks ? ' checked' : '') + ' /><span>Resolve symlinks before mapping (for a library that points at a tree of links)</span></label>' +
            '<div class="ep-signal-row"><span>Longest list written</span><input type="number" class="emby-input ep-number ep-max-entries" data-index="' + index + '" min="1" max="5000" step="1" value="' + toInt(target.MaxEntries, 300) + '" /></div>' +
            '<label class="checkboxContainer"><input is="emby-checkbox" type="checkbox" class="ep-dry-run" data-index="' + index + '"' + (target.DryRun ? ' checked' : '') + ' /><span>Dry run: build and report, write no file</span></label>' +
        '</details>' +
        '<button is="emby-button" type="button" class="raised qg-delete-btn ep-remove-target" data-index="' + index + '" style="background:#c62828 !important;color:#fff !important;border-color:#c62828 !important;"><span>Remove encoder</span></button>' +
    '</details>';
}

function renderEncodeTargets(view) {
    var container = view.querySelector('#encodeTargetsContainer');
    if (!container) {
        return;
    }

    container.innerHTML = config.EncodeTargets.length
        ? config.EncodeTargets.map(buildTargetCard).join('')
        : getEmptyState('No encoder yet. Click "Add encoder" to create one.');

    container.querySelectorAll('.ep-card').forEach(function (card) {
        card.addEventListener('toggle', function (event) {
            if (event.target === card) {
                rememberCard(card.dataset.id, 'open', card.open);
            }
        });
    });

    container.querySelectorAll('.ep-card-advanced').forEach(function (details) {
        details.addEventListener('toggle', function () {
            var card = details.closest('.ep-card');
            rememberCard(card.dataset.id, 'advanced', details.open);
            card.classList.toggle('ep-show-advanced', details.open);
        });
    });
}

function rememberCard(id, key, value) {
    epCardState[id] = epCardState[id] || { open: false, advanced: false };
    epCardState[id][key] = value;
}

function renderEncodePriority(view) {
    var body = view.querySelector('#encodePriorityBody');
    var datalist = view.querySelector('#epLibraryLocations');
    if (!body) {
        return;
    }

    view.querySelector('#enableEncodePriority').checked = config.EnableEncodePriority;
    body.dataset.off = String(!config.EnableEncodePriority);
    view.querySelector('#epNowPlaying').checked = Boolean(config.PriorityNowPlaying);
    view.querySelector('#epContinueWatching').checked = Boolean(config.PriorityContinueWatching);
    view.querySelector('#epNextUp').checked = Boolean(config.PriorityNextUp);
    view.querySelector('#epNextUpDepth').value = config.PriorityNextUpDepth;
    view.querySelector('#epFavourites').checked = Boolean(config.PriorityFavourites);
    view.querySelector('#epWatchedWithinDays').value = config.PriorityWatchedWithinDays;
    view.querySelector('#epRefreshOnPlayback').checked = Boolean(config.PriorityRefreshOnPlayback);
    view.querySelector('#epDebounceMinutes').value = config.PriorityDebounceMinutes;
    view.querySelector('#epContinueWatchingPerUser').value = config.PriorityContinueWatchingPerUser;
    view.querySelector('#epNextUpShowsPerUser').value = config.PriorityNextUpShowsPerUser;
    view.querySelector('#epNextUpIncludeSpecials').checked = Boolean(config.PriorityNextUpIncludeSpecials);
    view.querySelector('#epFavouritesPerUser').value = config.PriorityFavouritesPerUser;
    view.querySelector('#epUnprobedNeedsEncode').checked = Boolean(config.PriorityUnprobedNeedsEncode);
    view.querySelector('#epRunBudgetSeconds').value = config.PriorityRunBudgetSeconds;
    if (datalist) {
        datalist.innerHTML = libraryLocations.map(function (location) {
            return '<option value="' + escapeAttribute(location) + '"></option>';
        }).join('');
    }

    renderEncodeTargets(view);
}

function checkedValues(card, selector) {
    return Array.prototype.map.call(card.querySelectorAll(selector + ':checked'), function (input) {
        return input.value;
    });
}

/**
 * The folder rows as typed, empty ones included, so each keeps the index its × button carries
 * and a re-render keeps a row just added. saveConfig drops the empty ones.
 */
export function readFolderRows(jellyfinPaths, encoderPaths) {
    return Array.prototype.map.call(jellyfinPaths, function (input, row) {
        return { JellyfinPath: input.value.trim(), EncoderPath: encoderPaths[row] ? encoderPaths[row].value.trim() : '' };
    });
}

/** Drops the folder rows left empty on both sides, before the page validates and saves. */
export function dropEmptyFolders(cfg) {
    (cfg.EncodeTargets || []).forEach(function (target) {
        target.Folders = (target.Folders || []).filter(function (folder) {
            return folder.JellyfinPath || folder.EncoderPath;
        });
    });
}

function collectEncodePriority(view) {
    var toggle = view.querySelector('#enableEncodePriority');
    if (!toggle) {
        return;
    }

    config.EnableEncodePriority = toggle.checked;
    config.PriorityNowPlaying = view.querySelector('#epNowPlaying').checked;
    config.PriorityContinueWatching = view.querySelector('#epContinueWatching').checked;
    config.PriorityNextUp = view.querySelector('#epNextUp').checked;
    config.PriorityNextUpDepth = toInt(view.querySelector('#epNextUpDepth').value, config.PriorityNextUpDepth);
    config.PriorityFavourites = view.querySelector('#epFavourites').checked;
    config.PriorityWatchedWithinDays = toInt(view.querySelector('#epWatchedWithinDays').value, config.PriorityWatchedWithinDays);
    config.PriorityRefreshOnPlayback = view.querySelector('#epRefreshOnPlayback').checked;
    config.PriorityDebounceMinutes = toInt(view.querySelector('#epDebounceMinutes').value, config.PriorityDebounceMinutes);
    config.PriorityContinueWatchingPerUser = toInt(view.querySelector('#epContinueWatchingPerUser').value, config.PriorityContinueWatchingPerUser);
    config.PriorityNextUpShowsPerUser = toInt(view.querySelector('#epNextUpShowsPerUser').value, config.PriorityNextUpShowsPerUser);
    config.PriorityNextUpIncludeSpecials = view.querySelector('#epNextUpIncludeSpecials').checked;
    config.PriorityFavouritesPerUser = toInt(view.querySelector('#epFavouritesPerUser').value, config.PriorityFavouritesPerUser);
    config.PriorityUnprobedNeedsEncode = view.querySelector('#epUnprobedNeedsEncode').checked;
    config.PriorityRunBudgetSeconds = toInt(view.querySelector('#epRunBudgetSeconds').value, config.PriorityRunBudgetSeconds);

    view.querySelectorAll('#encodeTargetsContainer .ep-card').forEach(function (card) {
        var target = config.EncodeTargets[parseInt(card.dataset.index, 10)];
        var jellyfinPaths;
        var encoderPaths;
        var mode;
        var audience;
        var outputPath;

        if (!target) {
            return;
        }

        jellyfinPaths = card.querySelectorAll('.ep-jellyfin-path');
        encoderPaths = card.querySelectorAll('.ep-encoder-path');
        target.Name = card.querySelector('.ep-name').value.trim();
        target.Enabled = card.querySelector('.ep-enabled').checked;
        target.Folders = readFolderRows(jellyfinPaths, encoderPaths);
        target.OutputHeight = toInt(card.querySelector('.ep-output-height').value, target.OutputHeight);
        mode = card.querySelector('.ep-output-mode:checked');
        target.OutputMode = mode ? mode.value : target.OutputMode;
        outputPath = card.querySelector('.ep-output-path');
        if (outputPath) {
            target.OutputPath = outputPath.value.trim();
        }

        audience = card.querySelector('.ep-audience-mode:checked');
        target.AudienceMode = audience ? audience.value : target.AudienceMode;
        if (card.querySelector('.ep-audience-policy')) {
            target.AudiencePolicyIds = checkedValues(card, '.ep-audience-policy');
        }

        if (card.querySelector('.ep-audience-user')) {
            target.AudienceUserIds = checkedValues(card, '.ep-audience-user');
        }

        if (card.querySelector('.ep-excluded-user')) {
            target.ExcludedUserIds = checkedValues(card, '.ep-excluded-user');
        }

        target.ResolveSymlinks = card.querySelector('.ep-resolve-symlinks').checked;
        target.MaxEntries = toInt(card.querySelector('.ep-max-entries').value, target.MaxEntries);
        target.DryRun = card.querySelector('.ep-dry-run').checked;
    });
}

function refreshEncodePriority(view) {
    collectFromDOM(view);
    renderEncodePriority(view);
    upgradeNativeWidgets(view);
}

function addEncodeTarget(view) {
    var target;
    if (!isLoaded) {
        return;
    }

    collectFromDOM(view);
    target = normalizeEncodeTarget({ Name: '', Folders: [{ JellyfinPath: '', EncoderPath: '' }] });
    epCardState[target.Id] = { open: true, advanced: false };
    config.EncodeTargets.push(target);
    renderEncodePriority(view);
    upgradeNativeWidgets(view);
    markDirty(view);
}

function handleEncodePriorityClick(view, button) {
    var index = parseInt(button.dataset.index, 10);
    var target;

    collectFromDOM(view);
    target = config.EncodeTargets[index];
    if (!target) {
        return;
    }

    if (button.classList.contains('ep-add-folder')) {
        target.Folders.push({ JellyfinPath: '', EncoderPath: '' });
    } else if (button.classList.contains('ep-remove-folder')) {
        target.Folders.splice(parseInt(button.dataset.row, 10), 1);
    } else if (button.classList.contains('ep-remove-target')) {
        if (!confirm('Remove encoder "' + (target.Name || 'Unnamed encoder') + '"? Its list file is removed on the next run.')) {
            return;
        }

        config.EncodeTargets.splice(index, 1);
    }

    renderEncodePriority(view);
    upgradeNativeWidgets(view);
    markDirty(view);
}

function formatTime(value) {
    if (!value) {
        return 'never';
    }

    var date = new Date(value);
    return isNaN(date.getTime()) ? String(value) : date.toLocaleString();
}

var RESULT_BADGES = {
    OK: 'OK',
    Unchanged: 'OK, unchanged',
    Warning: 'Warning',
    Error: 'Error',
    TimedOut: 'Timed out, previous list kept',
    DryRun: 'Dry run'
};

function badgeClass(badge) {
    if (badge === 'OK' || badge === 'OK, unchanged') {
        return 'ep-badge ep-badge-ok';
    }

    if (badge === 'Warning' || badge === 'Timed out, previous list kept') {
        return 'ep-badge ep-badge-warning';
    }

    return badge === 'Error' ? 'ep-badge ep-badge-error' : 'ep-badge';
}

function findStatusTarget(targets, id) {
    return (targets || []).find(function (candidate) {
        return candidate.Id === id;
    }) || null;
}

function formatDuration(ms) {
    return Math.max(0, Math.round((ms || 0) / 1000)) + ' s';
}

function countsLine(counts) {
    var c = counts || {};
    return [
        [c.Viewers, 'viewers'],
        [c.DemandItems, 'demand items'],
        [c.Gaps, 'gaps'],
        [c.Listed, 'listed'],
        [c.Covered, 'covered'],
        [c.HeightUnknown, 'height unknown'],
        [c.Unmapped, 'unmapped'],
        [c.CutByLimit, 'cut by the limit']
    ].map(function (pair) {
        return (pair[0] || 0) + ' ' + pair[1];
    }).join(' · ');
}

var SEVEN_DAYS_MS = 7 * 24 * 60 * 60 * 1000;

function formatSpan(ms) {
    var minutes = Math.max(0, Math.round(ms / 60000));
    var hours = Math.floor(minutes / 60);
    return hours > 0 ? hours + ' h ' + (minutes % 60) + ' m' : minutes + ' m';
}

/**
 * The end-to-end proof for one encoder over the last 7 days: how many items were listed, how
 * many gained a within-cap version (with the median wait), how many a capped viewer then played
 * within their cap, and how many were played above it although a smaller version existed.
 */
export function buildSevenDaySummary(target, covered, nowMs) {
    var since = nowMs - SEVEN_DAYS_MS;
    var listed = {};
    var mine = (covered || []).filter(function (record) {
        return record.TargetId === target.Id && Date.parse(record.CoveredAt) >= since;
    });
    var waits;
    var median;

    (target.Entries || []).forEach(function (entry) {
        if (Date.parse(entry.FirstListedAt) >= since) {
            listed[entry.ItemId] = true;
        }
    });
    mine.forEach(function (record) {
        if (Date.parse(record.ListedAt) >= since) {
            listed[record.ItemId] = true;
        }
    });

    waits = mine.map(function (record) {
        return Date.parse(record.CoveredAt) - Date.parse(record.ListedAt);
    }).sort(function (a, b) {
        return a - b;
    });
    median = waits.length
        ? (waits.length % 2 ? waits[(waits.length - 1) / 2] : (waits[waits.length / 2 - 1] + waits[waits.length / 2]) / 2)
        : null;

    return 'Last 7 days: ' + Object.keys(listed).length + ' listed · ' +
        mine.length + ' covered' + (median === null ? '' : ' (median ' + formatSpan(median) + ')') + ' · ' +
        mine.filter(function (record) {
            return record.ServedAt;
        }).length + ' served within cap · ' +
        mine.filter(function (record) {
            return record.ServedOverCapAt;
        }).length + ' played over the cap';
}

/** The list as a table: rank, title, the path as the encoder sees it, the height, why. */
export function buildListTable(entries) {
    if (!(entries || []).length) {
        return '<p class="fieldDescription">The list is empty: nothing capped viewers are watching needs an encode.</p>';
    }

    return '<table class="ep-preview-table"><thead><tr><th>#</th><th>Title</th><th>Path as the encoder sees it</th><th>Height</th><th>Why</th></tr></thead><tbody>' +
        entries.map(function (entry) {
            return '<tr>' +
                '<td>' + escapeHtml(entry.Rank) + '</td>' +
                '<td>' + escapeHtml(entry.Title || 'Item no longer in the library') + '</td>' +
                '<td><code>' + escapeHtml(entry.Path) + '</code></td>' +
                '<td>' + (entry.Height ? escapeHtml(entry.Height) + 'p' : 'unknown') + '</td>' +
                '<td>' + (entry.Reasons || []).map(function (reason) {
                    return '<span class="ep-chip">' + escapeHtml(reason) + '</span>';
                }).join(' ') + (entry.Users > 1 ? ' <span class="ep-chip">' + escapeHtml(entry.Users) + ' viewers</span>' : '') + '</td>' +
            '</tr>';
        }).join('') +
        '</tbody></table>';
}

/**
 * The status panel per target. With the plugin's status endpoint it shows the last run, the
 * counts, the findings, the current list and the last preview; without it, it falls back to
 * what Jellyfin already ships: the scheduled task's last result and the task's activity entries.
 */
export function buildStatusPanels(cfg, task, entries, status, nowMs) {
    var result = task && task.LastExecutionResult;
    var now = nowMs || Date.now();
    var preview = status && status.Preview;

    if (!(cfg.EncodeTargets || []).length) {
        return '<p class="fieldDescription">No encoder configured.</p>';
    }

    return cfg.EncodeTargets.map(function (target) {
        var mine = (entries || []).filter(function (entry) {
            return entry.Type === ENCODE_PRIORITY_ACTIVITY_TYPE && String(entry.ShortOverview || '').indexOf((target.Name || '') + ':') === 0;
        });
        var latest = mine[0];
        var state = status ? findStatusTarget(status.Targets, target.Id) : null;
        var previewed = preview ? findStatusTarget(preview.Targets, target.Id) : null;
        var badge;
        var html;

        if (!cfg.EnableEncodePriority || target.Enabled === false) {
            badge = 'Off';
        } else if (state && state.Result) {
            badge = RESULT_BADGES[state.Result] || state.Result;
        } else if (!result) {
            badge = 'Waiting for first run';
        } else if (result.Status === 'Failed' || result.Status === 'Aborted') {
            badge = 'Error';
        } else if (latest && latest.Overview && latest.Overview.indexOf('TimedOut') !== -1) {
            badge = 'Timed out, previous list kept';
        } else if (latest && latest.Severity && latest.Severity !== 'Information') {
            badge = 'Warning';
        } else {
            badge = latest ? 'OK' : 'OK, unchanged';
        }

        html = '<div class="ep-status-panel">' +
            '<strong>' + escapeHtml(target.Name || 'Unnamed encoder') + '</strong>' +
            '<span class="' + badgeClass(badge) + '">' + escapeHtml(badge) + '</span>';

        if (state) {
            html += '<div class="fieldDescription">Last run: ' + escapeHtml(formatTime(state.LastRunUtc)) +
                    ' (' + formatDuration(state.DurationMs) + ', ' + escapeHtml(state.Trigger || 'scheduled') + ')' +
                    ' · last write: ' + escapeHtml(formatTime(state.LastWriteUtc)) +
                    (state.OutputPath ? ' · ' + escapeHtml(state.OutputPath) : '') +
                    (state.Error ? ' · ' + escapeHtml(state.Error) : '') +
                '</div>' +
                '<div>' + escapeHtml(countsLine(state.Counts)) + '</div>' +
                '<div>' + escapeHtml(buildSevenDaySummary(state, status.Covered, now)) + '</div>' +
                ((state.Findings || []).length
                    ? '<ul>' + state.Findings.map(function (finding) {
                        return '<li><strong>' + escapeHtml(finding.Code) + '</strong>: ' + escapeHtml(finding.Message) +
                            ((finding.Examples || []).length ? '<br /><code>' + finding.Examples.map(escapeHtml).join('</code><br /><code>') + '</code>' : '') +
                            '</li>';
                    }).join('') + '</ul>'
                    : '') +
                '<details class="ep-list"><summary>Current list (' + (state.Entries || []).length + ' entries)</summary>' + buildListTable(state.Entries) + '</details>';
        } else {
            html += '<div class="fieldDescription">Last run: ' + escapeHtml(formatTime(result && result.EndTimeUtc)) +
                    (result && result.StartTimeUtc && result.EndTimeUtc
                        ? ' (' + Math.max(0, Math.round((new Date(result.EndTimeUtc) - new Date(result.StartTimeUtc)) / 1000)) + ' s)'
                        : '') +
                    (result && result.ErrorMessage ? ' · ' + escapeHtml(result.ErrorMessage) : '') +
                    (latest ? ' · last change: ' + escapeHtml(formatTime(latest.Date)) : '') +
                '</div>' +
                (latest ? '<div>' + escapeHtml(latest.ShortOverview) + '</div>' : '') +
                (latest && latest.Overview
                    ? '<ul>' + latest.Overview.split('\n').map(function (line) {
                        return '<li>' + escapeHtml(line) + '</li>';
                    }).join('') + '</ul>'
                    : '');
        }

        if (previewed) {
            html += '<details class="ep-list"><summary>Preview from ' + escapeHtml(formatTime(preview.RanAtUtc)) +
                (previewed.Result === 'TimedOut' ? ', timed out' : ' (' + (previewed.Entries || []).length + ' entries, nothing written)') +
                '</summary>' +
                '<div>' + escapeHtml(countsLine(previewed.Counts)) + '</div>' +
                ((previewed.Findings || []).length
                    ? '<ul>' + previewed.Findings.map(function (finding) {
                        return '<li><strong>' + escapeHtml(finding.Code) + '</strong>: ' + escapeHtml(finding.Message) + '</li>';
                    }).join('') + '</ul>'
                    : '') +
                buildListTable(previewed.Entries) +
                '</details>';
        }

        return html + '</div>';
    }).join('');
}

function getJson(path, params) {
    return ApiClient.getJSON(ApiClient.getUrl(path, params));
}

function findEncodePriorityTask() {
    return getJson('ScheduledTasks').then(function (tasks) {
        return (tasks || []).find(function (task) {
            return task.Key === ENCODE_PRIORITY_TASK_KEY;
        }) || null;
    });
}

function loadEncodePriorityStatus(view) {
    var container = view.querySelector('#encodePriorityStatus');
    if (!container) {
        return Promise.resolve();
    }

    return Promise.all([
        findEncodePriorityTask(),
        getJson('System/ActivityLog/Entries', { type: ENCODE_PRIORITY_ACTIVITY_TYPE, limit: 100 }).catch(function () {
            return { Items: [] };
        }),
        getJson('QualityGate/EncodePriority/Status').catch(function () {
            return null;
        })
    ]).then(function (results) {
        var status = results[2];
        if (status && status.DataPath && status.DataPath !== epDataPath) {
            epDataPath = status.DataPath;
            updateResolvedPaths(view);
        }

        container.innerHTML = buildStatusPanels(config, results[0], (results[1] && results[1].Items) || [], status);
    }).catch(function (err) {
        container.innerHTML = '<p class="fieldDescription ep-error">Could not load the status: ' + escapeHtml(err && err.message ? err.message : String(err)) + '</p>';
    });
}

function runEncodePriorityNow(view) {
    findEncodePriorityTask().then(function (task) {
        if (!task) {
            throw new Error('the scheduled task was not found; restart Jellyfin after installing the plugin');
        }

        return ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('ScheduledTasks/Running/' + task.Id) });
    }).then(function () {
        Dashboard.alert('Encode priority run started. Save first if you changed anything: the run uses the saved settings.');
        setTimeout(function () {
            loadEncodePriorityStatus(view);
        }, 3000);
    }).catch(function (err) {
        Dashboard.alert('Could not start the run: ' + (err && err.message ? err.message : String(err)));
    });
}

/** Shows the real Data folder path on each card once the status has told the page where it is. */
function updateResolvedPaths(view) {
    view.querySelectorAll('#encodeTargetsContainer .ep-card').forEach(function (card) {
        var target = config.EncodeTargets[parseInt(card.dataset.index, 10)];
        var path = card.querySelector('.ep-resolved-path');
        var setup = card.querySelector('.ep-setup-text');
        var output;
        if (!target || !path || target.OutputMode !== 'DataFolder') {
            return;
        }

        output = resolveOutputPath(target, epDataPath);
        path.textContent = output.path;
        if (setup) {
            setup.textContent = encoderSetupText(target, output.path);
        }
    });
}

function previewEncodePriority(view) {
    ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('QualityGate/EncodePriority/Preview') }).then(function () {
        Dashboard.alert('Preview queued. It builds every enabled encoder\'s list from the saved settings and writes nothing. It appears under Status when it finishes.');
        setTimeout(function () {
            loadEncodePriorityStatus(view);
        }, 3000);
    }).catch(function (err) {
        Dashboard.alert('Could not queue the preview: ' + (err && err.message ? err.message : (err && err.status ? 'HTTP ' + err.status : String(err))));
    });
}

function loadLibraryLocations() {
    return getJson('Library/VirtualFolders').then(function (folders) {
        libraryLocations = [];
        (folders || []).forEach(function (folder) {
            (folder.Locations || []).forEach(function (location) {
                if (libraryLocations.indexOf(location) === -1) {
                    libraryLocations.push(location);
                }
            });
        });
    }).catch(function () {
        libraryLocations = [];
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

    view.querySelector('#btnAddEncodeTarget').addEventListener('click', function () {
        addEncodeTarget(view);
    });

    view.querySelector('#btnEncodePriorityRunNow').addEventListener('click', function () {
        runEncodePriorityNow(view);
    });

    view.querySelector('#btnEncodePriorityPreview').addEventListener('click', function () {
        previewEncodePriority(view);
    });

    view.querySelector('#encodeTargetsContainer').addEventListener('click', function (event) {
        var button = event.target.closest('.ep-add-folder, .ep-remove-folder, .ep-remove-target');
        if (button && isLoaded) {
            event.preventDefault();
            handleEncodePriorityClick(view, button);
        }
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

        if (event.target.matches('#defaultPolicySelect, #apiKeyPolicySelect, .policy-enabled, .policy-fallback-transcode, .policy-name, .policy-max-height')) {
            refreshComputedPreview(view);
        }

        if (event.target.matches('#enableEncodePriority, .ep-name, .ep-enabled, .ep-jellyfin-path, .ep-encoder-path, .ep-output-height, .ep-output-mode, .ep-output-path, .ep-audience-mode, .ep-dry-run')) {
            refreshEncodePriority(view);
        }

        markDirty(view);
    });

    view.addEventListener('viewshow', function () {
        loadConfig(view);
    });
}
