/**
 * Manitto Ops Dashboard — app.js
 * 5초 폴링 + Page Visibility API 기반 인스턴스 모니터
 * #72: 한글명 표시 + 글로벌 매칭 Config 패널
 */

const POLL_INTERVAL_MS = 5000;

let _selectedMatchingId = null;
let _pollTimer = null;
let _seqList = [];      // 강제 폐쇄 시퀀스 (AreaType 정수 목록)
let _eventLastSeq = 0;  // 이벤트 로그 폴링 진행 커서
let _eventEntries = []; // 누적된 이벤트 (최근 200개 유지)

// ─── 직책 정의 ──────────────────────────────────────────────────────────────

const JOB_TITLES = [
    { id: 1, name: '방송부원' },
    { id: 2, name: '선도부원' },
    { id: 3, name: '도서위원' },
    { id: 4, name: '체육부장' },
    { id: 5, name: '과학부원' },
    { id: 6, name: '미화부원' },
    { id: 7, name: '학생회장' },
    { id: 8, name: '보건부원' },
];

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
        // 풀 스냅샷 endpoint 사용 (폐쇄 스케줄 + 미션 전체 단계 포함)
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

async function fetchMatchingConfig() {
    try {
        const res = await fetch('/api/matching-config');
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return await res.json();
    } catch (e) {
        console.error('[ops] fetchMatchingConfig 실패:', e);
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

        // 진행 로그 — 마지막 Seq 이후만 증분 폴링
        const evResp = await fetchInstanceEvents(_selectedMatchingId, _eventLastSeq);
        if (evResp && evResp.events && evResp.events.length > 0) {
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
    _eventLastSeq = 0;
    _eventEntries = [];
    document.getElementById('event-log').innerHTML = '<p class="text-gray-600">로그 대기 중...</p>';
    document.getElementById('event-count').textContent = '0건';

    const detail = await fetchInstance(matchingId);
    if (detail) renderDetail(detail);

    // 첫 로그 batch 즉시 로드 (since=0 → 전체)
    const evResp = await fetchInstanceEvents(matchingId, 0);
    if (evResp && evResp.events) {
        _eventEntries = evResp.events.slice().reverse();
        for (const ev of _eventEntries) {
            if (ev.seq > _eventLastSeq) _eventLastSeq = ev.seq;
        }
        renderEventLog();
    }

    document.getElementById('detail-section').classList.remove('hidden');
}

function closeDetail() {
    _selectedMatchingId = null;
    _eventLastSeq = 0;
    _eventEntries = [];
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
    const configInfo = document.getElementById('closure-config-info');

    if (!closure) {
        section.classList.add('hidden');
        return;
    }
    section.classList.remove('hidden');

    // 인스턴스 config 표시 (기본값과 다를 때만)
    const defaultStart = 300, defaultInterval = 180;
    if (closure.startDelaySec !== defaultStart || closure.intervalSec !== defaultInterval) {
        configInfo.textContent = `적용 config: 시작딜레이 ${closure.startDelaySec}s · 간격 ${closure.intervalSec}s`;
        configInfo.classList.remove('hidden');
    } else {
        configInfo.classList.add('hidden');
    }

    // 카운트다운 표시
    if (closure.nextClosureSecondsLeft >= 0) {
        const mins = Math.floor(closure.nextClosureSecondsLeft / 60);
        const secs = closure.nextClosureSecondsLeft % 60;
        const timeStr = `${String(mins).padStart(2, '0')}:${String(secs).padStart(2, '0')}`;
        countdown.textContent = closure.warningActive
            ? `경고! 다음 폐쇄까지 ${timeStr}`
            : `다음 폐쇄까지 ${timeStr}`;
        countdown.className = closure.warningActive
            ? 'text-xs text-red-400 mb-2 font-bold'
            : 'text-xs text-yellow-400 mb-2';
        countdown.classList.remove('hidden');
    } else {
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

// AreaType 정수 → 한글 라벨 (fallback — 서버 응답 한글명 없을 때)
const AREA_LABELS = {
    10:'행정실', 11:'1층복도', 12:'교무실', 13:'강당', 14:'창고',
    20:'교실2', 21:'2층복도', 22:'도서관',
    30:'교실3', 31:'3층복도', 32:'고사실',
    40:'교실4', 41:'4층복도', 42:'방송실',
    1:'쓰레기장', 2:'운동장', 100:'캠프'
};
function areaTypeLabel(t) { return AREA_LABELS[t] ?? `Area${t}`; }

// ─── 플레이어 행 렌더링 ────────────────────────────────────────────────────

function buildPlayerRow(p) {
    const staminaPct = Math.max(0, Math.min(100, p.stamina));
    const corruptPct = Math.max(0, Math.min(100, p.corruption));
    const botBadge = p.isBot ? '<span class="ml-1 text-xs bg-gray-700 px-1 rounded">BOT</span>' : '';
    const statusClass = `status-${p.manittoStatus}`;
    const rowClass = p.isEliminated ? 'opacity-40' : '';
    const jobStr = p.jobTitle || '—';

    // 미션 단계 — allSteps 있으면 가로 흐름, 없으면 숫자 fallback
    let missionStr;
    if (p.allSteps && p.allSteps.length > 0) {
        missionStr = buildMissionSteps(p.allSteps, p.missionCompleted);
    } else {
        missionStr = p.missionCompleted
            ? '<span class="text-green-400">완료</span>'
            : `${p.missionStep}/${p.missionTotalSteps}`;
    }

    return `
        <tr class="border-b border-gray-800 ${rowClass}">
            <td class="py-2 pr-4">${p.playerId}${botBadge}</td>
            <td class="py-2 pr-4 text-xs text-gray-300">${jobStr}</td>
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
            <td class="py-2 pr-4">${missionStr}</td>
            <td class="py-2 pr-4 text-xs text-gray-400">${p.manittoOfMe ?? '—'}</td>
            <td class="py-2 text-xs text-gray-400">${p.targetPlayerId || '—'}</td>
        </tr>
    `;
}

function buildMissionSteps(steps, allCompleted) {
    if (allCompleted) {
        return steps.map(s => {
            const tip = buildStepTooltip(s);
            return `<span class="step-done text-xs" title="${tip}">완료${s.order}</span>`;
        }).join('<span class="text-gray-600 mx-0.5">→</span>');
    }
    return steps.map(s => {
        const tip = buildStepTooltip(s);
        if (s.isCompleted) {
            return `<span class="step-done text-xs" title="${tip}">완료${s.order}</span>`;
        }
        if (s.isCurrent) {
            const label = s.targetAreaName || areaTypeLabel(s.targetAreaType);
            const desc = s.description ? `: ${s.description}` : '';
            return `<span class="step-current text-xs" title="${tip}">▶${s.order} ${label}${desc}</span>`;
        }
        return `<span class="step-future text-xs" title="${tip}">${s.order}</span>`;
    }).join('<span class="text-gray-700 mx-0.5">→</span>');
}

function buildStepTooltip(s) {
    const areaName = s.targetAreaName || areaTypeLabel(s.targetAreaType);
    const parts = [`${s.order}단계: ${areaName}`];
    if (s.description) parts.push(s.description);
    if (s.targetObjectName) parts.push(`대상: ${s.targetObjectName}`);
    return parts.join('\n').replace(/"/g, '&quot;');
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

// ─── 글로벌 매칭 Config 패널 ────────────────────────────────────────────────

async function loadMatchingConfig() {
    const cfg = await fetchMatchingConfig();
    if (!cfg) return;
    renderConfigStatus(cfg);

    // 현재 값으로 폼 초기화
    document.getElementById('cfg-start-delay').value = cfg.startDelaySec ?? 300;
    document.getElementById('cfg-interval').value = cfg.intervalSec ?? 180;

    if (cfg.forcedSequence && cfg.forcedSequence.length > 0) {
        document.getElementById('seq-forced').checked = true;
        _seqList = [...cfg.forcedSequence];
        document.getElementById('seq-forced-panel').classList.remove('hidden');
        renderSeqList();
    }

    if (cfg.forcedJobs && cfg.forcedJobs.length > 0) {
        document.getElementById('job-forced').checked = true;
        document.getElementById('job-forced-panel').classList.remove('hidden');
        document.getElementById('job-random-info').classList.add('hidden');
        // 체크박스 반영
        cfg.forcedJobs.forEach(id => {
            const cb = document.getElementById(`job-cb-${id}`);
            if (cb) cb.checked = true;
        });
    }
}

function renderConfigStatus(cfg) {
    const el = document.getElementById('config-status');
    const seqStr = cfg.forcedSequence && cfg.forcedSequence.length > 0
        ? cfg.forcedSequence.map(a => areaTypeLabel(a)).join('→')
        : '무작위';
    const jobStr = cfg.forcedJobs && cfg.forcedJobs.length > 0
        ? cfg.forcedJobs.map(j => JOB_TITLES.find(t => t.id === j)?.name ?? `직책${j}`).join(', ')
        : '무작위 8개';

    el.innerHTML = `시작딜레이: <span class="text-white">${cfg.startDelaySec}s</span>&nbsp;&nbsp;`
        + `폐쇄간격: <span class="text-white">${cfg.intervalSec}s</span>&nbsp;&nbsp;`
        + `폐쇄순서: <span class="text-yellow-300">${seqStr}</span>&nbsp;&nbsp;`
        + `직책풀: <span class="text-blue-300">${jobStr}</span>`;
}

function buildJobCheckboxes() {
    const container = document.getElementById('job-checkboxes');
    container.innerHTML = JOB_TITLES.map(j => `
        <label class="flex items-center gap-2 text-xs cursor-pointer">
            <input type="checkbox" id="job-cb-${j.id}" value="${j.id}" class="accent-blue-500" />
            ${j.name}
        </label>
    `).join('');
}

function onSeqModeChange() {
    const forced = document.getElementById('seq-forced').checked;
    document.getElementById('seq-forced-panel').classList.toggle('hidden', !forced);
}

function onJobModeChange() {
    const forced = document.getElementById('job-forced').checked;
    document.getElementById('job-forced-panel').classList.toggle('hidden', !forced);
    document.getElementById('job-random-info').classList.toggle('hidden', forced);
}

function addSeqArea(areaType) {
    _seqList.push(areaType);
    renderSeqList();
}

function clearSeqList() {
    _seqList = [];
    renderSeqList();
}

function renderSeqList() {
    const el = document.getElementById('seq-list');
    if (_seqList.length === 0) {
        el.textContent = '순서 없음';
    } else {
        el.innerHTML = _seqList.map((a, i) =>
            `<span class="inline-block bg-gray-700 px-1.5 py-0.5 rounded mr-1 cursor-pointer hover:bg-red-900"
                   onclick="_seqList.splice(${i},1);renderSeqList()" title="클릭하여 제거">${areaTypeLabel(a)}</span>`
        ).join('<span class="text-gray-600">→</span>');
    }
}

async function applyClosureConfig() {
    const startDelaySec = parseInt(document.getElementById('cfg-start-delay').value);
    const intervalSec = parseInt(document.getElementById('cfg-interval').value);
    if (isNaN(startDelaySec) || isNaN(intervalSec)) { showToast('입력값을 확인하세요.', true); return; }
    const body = { startDelaySec, intervalSec };
    const res = await postConfig('/api/matching-config/closure', body);
    if (res) { showToast(res.message || '적용 완료'); if (res.config) renderConfigStatus(res.config); }
}

async function resetClosureConfig() {
    const res = await postConfig('/api/matching-config/closure', { resetAll: true });
    if (res) {
        showToast(res.message || '기본값 복원 완료');
        if (res.config) {
            renderConfigStatus(res.config);
            document.getElementById('cfg-start-delay').value = res.config.startDelaySec;
            document.getElementById('cfg-interval').value = res.config.intervalSec;
        }
        _seqList = [];
        renderSeqList();
        document.getElementById('seq-random').checked = true;
        onSeqModeChange();
    }
}

async function applyForcedSequence() {
    if (_seqList.length === 0) { showToast('순서를 먼저 지정하세요.', true); return; }
    const res = await postConfig('/api/matching-config/closure', { sequence: _seqList });
    if (res) { showToast(res.message || '순서 적용 완료'); if (res.config) renderConfigStatus(res.config); }
}

async function applyJobPool() {
    const checked = [...document.querySelectorAll('#job-checkboxes input:checked')].map(cb => parseInt(cb.value));
    if (checked.length === 0) { showToast('하나 이상의 직책을 선택하세요.', true); return; }
    const res = await postConfig('/api/matching-config/job-pool', { jobs: checked });
    if (res) { showToast(res.message || '직책 풀 적용 완료'); if (res.config) renderConfigStatus(res.config); }
}

async function resetJobPool() {
    const res = await postConfig('/api/matching-config/job-pool', { jobs: null });
    if (res) {
        showToast(res.message || '직책 풀 무작위 복원 완료');
        if (res.config) renderConfigStatus(res.config);
        document.querySelectorAll('#job-checkboxes input').forEach(cb => { cb.checked = false; });
        document.getElementById('job-random').checked = true;
        onJobModeChange();
    }
}

async function postConfig(url, body) {
    try {
        const res = await fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return await res.json();
    } catch (e) {
        console.error('[ops] postConfig 실패:', e);
        showToast('서버 오류 — 콘솔 확인', true);
        return null;
    }
}

// ─── 토스트 ─────────────────────────────────────────────────────────────────

function showToast(msg, isError = false) {
    const el = document.getElementById('toast');
    el.textContent = msg;
    el.className = `fixed top-6 right-6 z-50 px-4 py-2 rounded shadow-lg text-sm ${isError ? 'bg-red-800 text-red-100' : 'bg-green-800 text-green-100'}`;
    el.classList.remove('hidden');
    el.style.opacity = '1';
    setTimeout(() => {
        el.style.opacity = '0';
        setTimeout(() => el.classList.add('hidden'), 300);
    }, 3000);
}

// ─── 진입 ──────────────────────────────────────────────────────────────────

buildJobCheckboxes();
loadMatchingConfig();
startPolling();
