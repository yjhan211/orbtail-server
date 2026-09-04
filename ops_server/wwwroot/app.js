/**
 * Manitto Ops Dashboard — app.js
 * 5초 폴링 + Page Visibility API 기반 인스턴스 모니터
 * 실제 매치 스냅샷 기반 폐쇄 순서·카운트다운 표시
 */

const POLL_INTERVAL_MS = 5000;

let _selectedMatchingId = null;
let _pollTimer = null;
let _countdownTimer = null;
let _closureCountdownState = null;
let _eventLastSeq = 0;  // 이벤트 로그 폴링 진행 커서
let _eventEntries = []; // 누적된 이벤트 (최근 200개 유지)

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
        // 풀 스냅샷 endpoint 사용 (폐쇄 스케줄 포함)
        const res = await fetch(`/api/instance/${matchingId}/full`);
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return await res.json();
    } catch (e) {
        console.error('[ops] fetchInstance 실패:', e);
        return null;
    }
}

async function fetchInstanceEvents(matchingId, sinceSeq) {
    try {
        const url = `/api/instance/${matchingId}/events`
            + (sinceSeq > 0 ? `?since=${sinceSeq}` : '');
        const res = await fetch(url);
        if (!res.ok) return null;
        return await res.json();
    } catch (e) {
        console.error('[ops] fetchInstanceEvents 실패:', e);
        return null;
    }
}

async function poll() {
    const data = await fetchInstances();
    if (data) renderInstances(data);

    // 상세 영역이 열려 있으면 갱신
    if (_selectedMatchingId !== null) {
        const matchingId = _selectedMatchingId;
        const stillActive = !data || data.instances.some(instance => instance.matchingId === matchingId);
        if (!stillActive) {
            if (_selectedMatchingId === matchingId) closeDetail();
        } else {
            const detail = await fetchInstance(matchingId);
            if (detail && _selectedMatchingId === matchingId) {
                renderDetail(detail);

                // 진행 로그 — 마지막 Seq 이후만 증분 폴링
                const evResp = await fetchInstanceEvents(matchingId, _eventLastSeq);
                if (_selectedMatchingId === matchingId && evResp?.events?.length > 0) {
                    // 서버가 최신순으로 반환 → 시간순 처리
                    const newOnes = evResp.events.slice().reverse();
                    for (const ev of newOnes) {
                        if (ev.seq > _eventLastSeq) _eventLastSeq = ev.seq;
                        _eventEntries.push(ev);
                    }
                    if (_eventEntries.length > 200) _eventEntries.splice(0, _eventEntries.length - 200);
                    renderEventLog();
                }
            }
        }
    }

    updateLastUpdate();
}

function startPolling() {
    if (_pollTimer === null) {
        poll();
        _pollTimer = setInterval(poll, POLL_INTERVAL_MS);
    }
    if (_countdownTimer === null) {
        _countdownTimer = setInterval(updateClosureCountdown, 1000);
    }
}

function stopPolling() {
    if (_pollTimer !== null) {
        clearInterval(_pollTimer);
        _pollTimer = null;
    }
    if (_countdownTimer !== null) {
        clearInterval(_countdownTimer);
        _countdownTimer = null;
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
    _eventLastSeq = 0;
    _eventEntries = [];
    document.getElementById('event-log').innerHTML = '<p class="text-gray-600">로그 대기 중...</p>';
    document.getElementById('event-count').textContent = '0건';

    const detail = await fetchInstance(matchingId);
    if (!detail || _selectedMatchingId !== matchingId) {
        if (_selectedMatchingId === matchingId) closeDetail();
        return;
    }
    renderDetail(detail);
    document.getElementById('detail-section').classList.remove('hidden');

    // 첫 로그 batch 즉시 로드 (since=0 → 전체)
    const evResp = await fetchInstanceEvents(matchingId, 0);
    if (_selectedMatchingId === matchingId && evResp?.events) {
        _eventEntries = evResp.events.slice().reverse();
        for (const ev of _eventEntries) {
            if (ev.seq > _eventLastSeq) _eventLastSeq = ev.seq;
        }
        renderEventLog();
    }

}

function closeDetail() {
    _selectedMatchingId = null;
    _closureCountdownState = null;
    _eventLastSeq = 0;
    _eventEntries = [];
    updateClosureCountdown();
    document.getElementById('detail-section').classList.add('hidden');
}

function renderDetail(snapshot) {
    document.getElementById('detail-title').textContent =
        `인스턴스 #${snapshot.matchingId} — ${snapshot.mapId || '맵 불명'} (${snapshot.aliveCount}/${snapshot.playerCount}명 생존)`;

    renderClosureSchedule(snapshot.closure);

    const tbody = document.getElementById('detail-tbody');
    tbody.innerHTML = snapshot.players
        .sort((a, b) => (a.isEliminated ? 1 : -1) - (b.isEliminated ? 1 : -1))
        .map(p => buildPlayerRow(p))
        .join('');
}

// ─── 구역 폐쇄 스케줄 렌더링 ───────────────────────────────────────────────

function renderClosureSchedule(closure) {
    const section = document.getElementById('closure-section');
    const countdown = document.getElementById('closure-countdown');
    const sequence = document.getElementById('closure-sequence');

    if (!closure) {
        _closureCountdownState = null;
        section.classList.add('hidden');
        return;
    }
    section.classList.remove('hidden');

    // 카운트다운 표시
    if (closure.nextClosureSecondsLeft >= 0) {
        _closureCountdownState = {
            secondsLeft: closure.nextClosureSecondsLeft,
            capturedAtMs: Date.now(),
            warningActive: closure.warningActive
        };
        updateClosureCountdown();
    } else {
        _closureCountdownState = null;
        countdown.classList.add('hidden');
    }

    // 시퀀스 태그 렌더링 — 서버가 보낸 한글명 우선 사용
    const closedSet = new Set(closure.closedAreaIds ?? []);
    const areaNames = closure.areaNames ?? [];

    sequence.innerHTML = (closure.closureSequence ?? []).map((areaType, idx) => {
        const isClosed = closedSet.has(areaType);
        const isNext = areaType === closure.nextClosureAreaType && !isClosed;
        const isWarning = isNext && closure.warningActive;
        let cls = 'closure-pending';
        if (isClosed) cls = 'closure-closed';
        else if (isWarning) cls = 'closure-warning';
        else if (isNext) cls = 'closure-next';

        // 서버 한글명이 있으면 사용, 없으면 fallback
        const label = (areaNames.length > idx && areaNames[idx]) ? areaNames[idx] : areaTypeLabel(areaType);
        return `<span class="text-xs px-2 py-1 rounded ${cls}" title="AreaType ${areaType}">${label}</span>`;
    }).join('');
}

function updateClosureCountdown() {
    const countdown = document.getElementById('closure-countdown');
    if (!_closureCountdownState || _selectedMatchingId === null) {
        countdown.classList.add('hidden');
        return;
    }

    const elapsedSeconds = Math.floor((Date.now() - _closureCountdownState.capturedAtMs) / 1000);
    const secondsLeft = Math.max(0, _closureCountdownState.secondsLeft - elapsedSeconds);
    const mins = Math.floor(secondsLeft / 60);
    const secs = secondsLeft % 60;
    const timeStr = `${String(mins).padStart(2, '0')}:${String(secs).padStart(2, '0')}`;
    countdown.textContent = _closureCountdownState.warningActive
        ? `경고! 다음 폐쇄까지 ${timeStr}`
        : `다음 폐쇄까지 ${timeStr}`;
    countdown.className = _closureCountdownState.warningActive
        ? 'text-xs text-red-400 mb-2 font-bold'
        : 'text-xs text-yellow-400 mb-2';
    countdown.classList.remove('hidden');
}

// AreaType 정수 → 한글 라벨 (fallback — 서버 응답 한글명 없을 때)
const AREA_LABELS = {
    1:'서쪽 쓰레기장', 2:'운동장', 7:'복도', 9:'동쪽 창고',
    10:'행정실', 12:'교무실', 13:'강당', 14:'서쪽 창고', 15:'동쪽 쓰레기장',
    20:'보건실', 22:'도서관', 30:'2-1', 32:'고사실', 40:'3-1', 42:'방송실',
    100:'캠프'
};
function areaTypeLabel(t) {
    const key = Number.parseInt(t, 10);
    return AREA_LABELS[key] || `Area${t}`;
}

// ─── 플레이어 행 렌더링 ────────────────────────────────────────────────────

function buildPlayerRow(p) {
    const staminaPct = Math.max(0, Math.min(100, p.stamina));
    const corruptPct = Math.max(0, Math.min(100, p.corruption));
    const botBadge = p.isBot ? '<span class="ml-1 text-xs bg-gray-700 px-1 rounded">BOT</span>' : '';
    const statusClass = `status-${p.playerMatchStatus}`;
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
            <td class="py-2 text-xs ${statusClass}">${p.playerMatchStatus}</td>
        </tr>
    `;
}

// ─── 진행 로그 렌더링 ──────────────────────────────────────────────────────

function renderEventLog() {
    const log = document.getElementById('event-log');
    const count = document.getElementById('event-count');
    if (_eventEntries.length === 0) {
        log.innerHTML = '<p class="text-gray-600">로그 없음</p>';
        count.textContent = '0건';
        return;
    }

    const lines = _eventEntries.map(ev => {
        const t = new Date(ev.timestampUnixMs);
        const ts = `${String(t.getHours()).padStart(2,'0')}:${String(t.getMinutes()).padStart(2,'0')}:${String(t.getSeconds()).padStart(2,'0')}`;
        const actor = ev.playerId === 0 ? '시스템' : (ev.isBot ? `Bot${ev.playerId}` : `P${ev.playerId}`);
        const cls = `ev-${ev.type}` + (ev.isBot ? ' ev-bot' : '');
        const safeDesc = (ev.description || '').replace(/&/g, '&amp;').replace(/</g, '&lt;');
        return `<div class="${cls} px-1 py-0.5 leading-tight">`
            + `<span class="text-gray-500">${ts}</span> `
            + `<span class="text-gray-400">[${ev.type}]</span> `
            + `<span class="text-gray-300">${actor}</span> `
            + `<span>${safeDesc}</span>`
            + `</div>`;
    });
    log.innerHTML = lines.join('');
    count.textContent = `${_eventEntries.length}건`;

    if (document.getElementById('event-autoscroll').checked) {
        log.scrollTop = log.scrollHeight;
    }
}

function updateLastUpdate() {
    const el = document.getElementById('last-update');
    el.textContent = `마지막 업데이트: ${new Date().toLocaleTimeString('ko-KR')}`;
}

// ─── 진입 ──────────────────────────────────────────────────────────────────

startPolling();
