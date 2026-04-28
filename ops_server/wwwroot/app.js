/**
 * Manitto Ops Dashboard — app.js
 * 5초 폴링 + Page Visibility API 기반 인스턴스 모니터
 */

const POLL_INTERVAL_MS = 5000;

let _selectedMatchingId = null;
let _pollTimer = null;

// ─── 폴링 ──────────────────────────────────────────────────────────────────

async function fetchInstances() {
    try {
        const res = await fetch('/api/instances');
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return await res.json();
    } catch (e) {
        console.error('[ops] fetchInstances 실패:', e);
        return null;
    }
}

async function fetchInstance(matchingId) {
    try {
        const res = await fetch(`/api/instance/${matchingId}`);
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return await res.json();
    } catch (e) {
        console.error('[ops] fetchInstance 실패:', e);
        return null;
    }
}

async function poll() {
    const data = await fetchInstances();
    if (data) renderInstances(data);

    // 상세 영역이 열려 있으면 갱신
    if (_selectedMatchingId !== null) {
        const detail = await fetchInstance(_selectedMatchingId);
        if (detail) renderDetail(detail);
    }

    updateLastUpdate();
}

function startPolling() {
    poll();
    _pollTimer = setInterval(poll, POLL_INTERVAL_MS);
}

function stopPolling() {
    if (_pollTimer !== null) {
        clearInterval(_pollTimer);
        _pollTimer = null;
    }
}

// Page Visibility API — 탭이 비활성일 때 폴링 중단
document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
        stopPolling();
    } else {
        startPolling();
    }
});

// ─── 렌더링 ────────────────────────────────────────────────────────────────

function renderInstances(data) {
    const grid = document.getElementById('instance-grid');
    const empty = document.getElementById('empty-state');
    const summary = document.getElementById('summary');

    summary.textContent = `인스턴스 ${data.totalInstances}개 · 플레이어 ${data.totalPlayers}명`;

    if (data.instances.length === 0) {
        empty.classList.remove('hidden');
        // 기존 카드 제거 (empty-state 제외)
        [...grid.children].forEach(c => { if (c.id !== 'empty-state') c.remove(); });
        return;
    }

    empty.classList.add('hidden');

    // 현재 카드 집합
    const existingIds = new Set(
        [...grid.querySelectorAll('[data-matching-id]')]
            .map(el => el.dataset.matchingId)
    );
    const incomingIds = new Set(data.instances.map(i => String(i.matchingId)));

    // 제거된 인스턴스 카드 삭제
    existingIds.forEach(id => {
        if (!incomingIds.has(id)) {
            grid.querySelector(`[data-matching-id="${id}"]`)?.remove();
        }
    });

    // 추가/업데이트
    data.instances.forEach(inst => {
        const idStr = String(inst.matchingId);
        let card = grid.querySelector(`[data-matching-id="${idStr}"]`);
        if (!card) {
            card = document.createElement('div');
            card.dataset.matchingId = idStr;
            card.className = 'bg-gray-900 border border-gray-700 rounded-lg p-4 cursor-pointer hover:border-gray-500 transition-colors card-active';
            card.addEventListener('click', () => openDetail(inst.matchingId));
            grid.appendChild(card);
        }
        card.innerHTML = buildCardHtml(inst);
    });
}

function buildCardHtml(inst) {
    const mins = Math.floor(inst.elapsedSeconds / 60);
    const secs = Math.floor(inst.elapsedSeconds % 60);
    const elapsed = `${String(mins).padStart(2, '0')}:${String(secs).padStart(2, '0')}`;
    const closedStr = inst.closedAreas.length > 0
        ? inst.closedAreas.join(', ')
        : '없음';

    return `
        <div class="flex items-center justify-between mb-2">
            <span class="text-xs text-gray-400">MatchingId</span>
            <span class="font-bold text-white">${inst.matchingId}</span>
        </div>
        <div class="flex items-center justify-between mb-1">
            <span class="text-xs text-gray-400">맵</span>
            <span class="text-sm">${inst.mapId || '—'}</span>
        </div>
        <div class="flex items-center justify-between mb-1">
            <span class="text-xs text-gray-400">플레이어</span>
            <span class="text-sm">${inst.aliveCount}<span class="text-gray-500">/${inst.playerCount}</span></span>
        </div>
        <div class="flex items-center justify-between mb-1">
            <span class="text-xs text-gray-400">경과</span>
            <span class="text-sm">${elapsed}</span>
        </div>
        <div class="mt-2 pt-2 border-t border-gray-700">
            <span class="text-xs text-gray-400">폐쇄구역: </span>
            <span class="text-xs text-red-400">${closedStr}</span>
        </div>
        <div class="mt-2 text-right text-xs text-gray-500">클릭하여 상세 보기</div>
    `;
}

// ─── 상세 ──────────────────────────────────────────────────────────────────

async function openDetail(matchingId) {
    _selectedMatchingId = matchingId;
    const detail = await fetchInstance(matchingId);
    if (detail) renderDetail(detail);
    document.getElementById('detail-section').classList.remove('hidden');
}

function closeDetail() {
    _selectedMatchingId = null;
    document.getElementById('detail-section').classList.add('hidden');
}

function renderDetail(snapshot) {
    document.getElementById('detail-title').textContent =
        `인스턴스 #${snapshot.matchingId} — ${snapshot.mapId || '맵 불명'} (${snapshot.aliveCount}/${snapshot.playerCount}명 생존)`;

    const tbody = document.getElementById('detail-tbody');
    tbody.innerHTML = snapshot.players
        .sort((a, b) => (a.isEliminated ? 1 : -1) - (b.isEliminated ? 1 : -1))
        .map(p => buildPlayerRow(p))
        .join('');
}

function buildPlayerRow(p) {
    const staminaPct = Math.max(0, Math.min(100, p.stamina));
    const corruptPct = Math.max(0, Math.min(100, p.corruption));
    const missionStr = p.missionCompleted
        ? '<span class="text-green-400">완료</span>'
        : `${p.missionStep}/${p.missionTotalSteps}`;
    const botBadge = p.isBot ? '<span class="ml-1 text-xs bg-gray-700 px-1 rounded">BOT</span>' : '';
    const statusClass = `status-${p.manittoStatus}`;
    const rowClass = p.isEliminated ? 'opacity-40' : '';

    return `
        <tr class="border-b border-gray-800 ${rowClass}">
            <td class="py-2 pr-4">${p.playerId}${botBadge}</td>
            <td class="py-2 pr-4 text-xs">${p.area}</td>
            <td class="py-2 pr-4">
                <div class="flex items-center gap-1">
                    <div class="w-16 bg-gray-700 rounded-full h-1.5">
                        <div class="bar-stamina h-1.5 rounded-full" style="width:${staminaPct}%"></div>
                    </div>
                    <span class="text-xs w-8">${p.stamina}</span>
                </div>
            </td>
            <td class="py-2 pr-4">
                <div class="flex items-center gap-1">
                    <div class="w-16 bg-gray-700 rounded-full h-1.5">
                        <div class="bar-corrupt h-1.5 rounded-full" style="width:${corruptPct}%"></div>
                    </div>
                    <span class="text-xs w-8">${p.corruption}</span>
                </div>
            </td>
            <td class="py-2 pr-4 text-xs ${statusClass}">${p.manittoStatus}</td>
            <td class="py-2 pr-4 text-xs">${missionStr}</td>
            <td class="py-2 pr-4 text-xs text-gray-400">${p.manittoOfMe ?? '—'}</td>
            <td class="py-2 text-xs text-gray-400">${p.targetPlayerId || '—'}</td>
        </tr>
    `;
}

function updateLastUpdate() {
    const el = document.getElementById('last-update');
    el.textContent = `마지막 업데이트: ${new Date().toLocaleTimeString('ko-KR')}`;
}

// ─── 진입 ──────────────────────────────────────────────────────────────────

startPolling();
