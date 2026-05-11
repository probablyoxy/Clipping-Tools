function connectWebSocket() {
    const ws = new WebSocket(wsUrl);

    ws.onopen = () => {
        ws.send(JSON.stringify({ action: 'web_listen' }));
    };

    const elements = {
        users: document.getElementById('stats-users'),
        clips_synced: document.getElementById('stats-sent'),
        clips_taken: document.getElementById('stats-received')
    };

    const currentVals = {
        users: parseInt(elements.users.innerText.replace(/,/g, '')) || 0,
        clips_synced: parseInt(elements.clips_synced.innerText.replace(/,/g, '')) || 0,
        clips_taken: parseInt(elements.clips_taken.innerText.replace(/,/g, '')) || 0
    };

    const intervals = { users: null, clips: null };

    ws.onmessage = (event) => {
        const data = JSON.parse(event.data);
        if (data.action === 'stats_update') {
            updateUsers(data.users);
            updateClips(data.clips_synced, data.clips_taken);
        }
    };

    ws.onclose = () => {
        setTimeout(connectWebSocket, 5000);
    };

    function animateElement(el) {
        el.classList.remove('swipe-animate');
        void el.offsetWidth;
        el.classList.add('swipe-animate');
    }

    function updateUsers(targetVal) {
        if (intervals.users) clearInterval(intervals.users);

        const diff = targetVal - currentVals.users;
        if (diff <= 0) {
            currentVals.users = targetVal;
            elements.users.innerText = currentVals.users;
            return;
        }

        const intervalMs = Math.floor(20000 / diff);
        let added = 0;

        intervals.users = setInterval(() => {
            currentVals.users++;
            elements.users.innerText = currentVals.users;
            animateElement(elements.users);

            added++;
            if (added >= diff) {
                clearInterval(intervals.users);
            }
        }, intervalMs);
    }

    function updateClips(targetSynced, targetTaken) {
        if (intervals.clips) clearInterval(intervals.clips);

        const diffSynced = targetSynced - currentVals.clips_synced;
        const diffTaken = targetTaken - currentVals.clips_taken;

        if (diffSynced <= 0) {
            currentVals.clips_synced = targetSynced;
            elements.clips_synced.innerText = currentVals.clips_synced;
            currentVals.clips_taken = targetTaken;
            elements.clips_taken.innerText = currentVals.clips_taken;
            return;
        }

        const intervalMs = Math.floor(20000 / diffSynced);
        const takenPerTick = diffTaken / diffSynced;
        
        let addedSynced = 0;
        let exactTaken = currentVals.clips_taken;

        intervals.clips = setInterval(() => {
            currentVals.clips_synced++;
            elements.clips_synced.innerText = currentVals.clips_synced;
            animateElement(elements.clips_synced);

            exactTaken += takenPerTick;
            currentVals.clips_taken = Math.round(exactTaken);
            elements.clips_taken.innerText = currentVals.clips_taken;
            animateElement(elements.clips_taken);

            addedSynced++;
            if (addedSynced >= diffSynced) {
                clearInterval(intervals.clips);
                currentVals.clips_taken = targetTaken;
                elements.clips_taken.innerText = currentVals.clips_taken;
            }
        }, intervalMs);
    }
}

connectWebSocket();

function getCookie(name) {
    const value = `; ${document.cookie}`;
    const parts = value.split(`; ${name}=`);
    if (parts.length === 2) {
        let cookieVal = parts.pop().split(';').shift();
        if (cookieVal.startsWith('"') && cookieVal.endsWith('"')) {
            cookieVal = cookieVal.slice(1, -1);
        }
        return cookieVal;
    }
    return null;
}

const profileBtn = document.getElementById('profile-btn');
const mainContent = document.getElementById('main-content');
const settingsContent = document.getElementById('settings-content');
const lockToggle = document.getElementById('lock-toggle');
const uuidList = document.getElementById('uuid-list');
const resetAllBtn = document.getElementById('reset-all-btn');
const logoutBtn = document.getElementById('logout-btn');

const modalOverlay = document.getElementById('modal-overlay');
const modalText = document.getElementById('modal-text');
const modalCancel = document.getElementById('modal-cancel');
const modalConfirm = document.getElementById('modal-confirm');

let modalCallback = null;
let doubleConfirmStep = 0;

function showModal(text, callback) {
    modalText.innerText = text;
    modalCallback = callback;
    doubleConfirmStep = 0;
    modalOverlay.style.display = 'flex';
}

modalCancel.onclick = () => {
    modalOverlay.style.display = 'none';
};

modalConfirm.onclick = () => {
    if (modalCallback === handleResetAll && doubleConfirmStep === 0) {
        doubleConfirmStep = 1;
        modalText.innerText = "Are you ABSOLUTELY sure? This will instantly disconnect all your apps.";
        return;
    }
    modalOverlay.style.display = 'none';
    if (modalCallback) modalCallback();
};

const token = getCookie('web_token');
const avatar = getCookie('web_avatar');

if (token && avatar) {
    profileBtn.src = decodeURIComponent(avatar);
}

profileBtn.onclick = () => {
    if (!token) {
        window.location.href = '/auth/login?state=web';
        return;
    }
    
    if (settingsContent.style.display === 'none') {
        mainContent.style.display = 'none';
        settingsContent.style.display = 'block';
        loadSettings();
    } else {
        mainContent.style.display = 'block';
        settingsContent.style.display = 'none';
    }
};

async function loadSettings() {
    const res = await fetch('/api/settings');
    if (res.ok) {
        const data = await res.json();
        lockToggle.checked = data.linking_locked;
        
        uuidList.innerHTML = '';
        data.uuids.forEach(uuid => {
            const div = document.createElement('div');
            div.className = 'uuid-item';
            div.innerHTML = `<span>${uuid}</span><span class="remove-uuid">×</span>`;
            div.querySelector('.remove-uuid').onclick = () => {
                showModal(`Remove access for app:\n${uuid}?`, () => handleRemoveUUID(uuid));
            };
            uuidList.appendChild(div);
        });
    }
}

lockToggle.onchange = async () => {
    await fetch('/api/settings/lock', {
        method: 'POST',
        headers: {'Content-Type': 'application/json'},
        body: JSON.stringify({locked: lockToggle.checked})
    });
};

async function handleRemoveUUID(uuid) {
    await fetch('/api/settings/remove_uuid', {
        method: 'POST',
        headers: {'Content-Type': 'application/json'},
        body: JSON.stringify({uuid})
    });
    loadSettings();
}

resetAllBtn.onclick = () => {
    showModal("Reset all App UUIDs? This will remove all authorized apps.", handleResetAll);
};

async function handleResetAll() {
    await fetch('/api/settings/reset', { method: 'POST' });
    loadSettings();
}

logoutBtn.onclick = () => {
    document.cookie = "web_token=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/;";
    document.cookie = "web_discord_id=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/;";
    document.cookie = "web_avatar=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/;";
    window.location.reload();
};