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

let _areaLabelsFromCsv = new Map();
const AREA_LABEL_OVERRIDES = {
    10:'행정실', 11:'1F', 12:'교무실', 13:'강당', 14:'창고',
    20:'1-1', 21:'2F', 22:'도서관',
    30:'2-1', 31:'3F', 32:'고사실',
    40:'3-1', 41:'4F', 42:'방송실',
    1:'쓰레기장', 2:'운동장', 100:'캠프'
};
function areaTypeLabel(t) {
    const key = Number.parseInt(t, 10);
    return _areaLabelsFromCsv.get(key) || AREA_LABEL_OVERRIDES[key] || AREA_LABELS[key] || `Area${t}`;
}

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

// ─── 기록 Storylet CSV 편집 ────────────────────────────────────────────────

let _storyletData = null;
let _storyletSelected = null; // { type: 'start'|'pool', nodeId, row }
let _storyletDirty = false;
let _storyletInteractablesByAreaObject = new Map();
let _storyletInteractablesById = new Map();
let _storyletInteractablesByObject = new Map();
let _storyletActionsByObject = new Map();
let _storyletActionsByGroup = new Map();
let _storyletGraphNodesByPart = new Map();
let _storyletRecipesById = new Map();
let _storyletRecipesByOutputPart = new Map();
let _storyletItemsByPart = new Map();
let _storyletItemsById = new Map();
let _storyletAutosaveTimer = null;
let _storyletSaving = false;
let _storyletImeComposing = false;
let _storyletObjectTypeLabels = new Map();

const STORYLET_AUTOSAVE_DELAY = 1200;

// 디바운스 자동저장 예약 (입력 후 일정 시간 무동작이면 silent 저장)
function scheduleStoryletAutosave() {
    if (_storyletAutosaveTimer) clearTimeout(_storyletAutosaveTimer);
    _storyletAutosaveTimer = setTimeout(() => {
        _storyletAutosaveTimer = null;
        if (_storyletDirty && !_storyletImeComposing && !_storyletSaving) {
            saveSelectedStorylet({ silent: true });
        }
    }, STORYLET_AUTOSAVE_DELAY);
}

// 대기 중인 자동저장을 즉시 실행 (행 이동/수동 저장/언로드 직전)
function flushStoryletAutosave() {
    if (_storyletAutosaveTimer) { clearTimeout(_storyletAutosaveTimer); _storyletAutosaveTimer = null; }
    if (_storyletDirty) return saveSelectedStorylet({ silent: false });
    return Promise.resolve();
}

// 상단 저장 상태바 갱신 (saving/saved/dirty/error/idle)
function updateStoryletSaveBar(state) {
    const el = document.getElementById('storylet-savebar');
    if (!el) return;
    const map = {
        saving: ['◐', '저장 중…', 'text-yellow-300'],
        saved: ['●', '저장됨', 'text-green-400'],
        dirty: ['○', '수정됨 · 곧 자동 저장', 'text-yellow-300'],
        error: ['✕', '저장 실패', 'text-red-400'],
        idle: ['', '', 'text-gray-600'],
    };
    const [icon, label, cls] = map[state] || map.idle;
    el.className = `text-xs ${cls}`;
    el.textContent = icon ? `${icon} ${label}` : '';
}

async function loadStorylets(forceToast = false) {
    try {
        const res = await fetch('/api/storylets');
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        _storyletData = await res.json();
        document.getElementById('storylet-loading').classList.add('hidden');
        document.getElementById('storylet-editor-grid').classList.remove('hidden');
        document.getElementById('storylet-csv-path').textContent = _storyletData.csvRoot || '';
        document.getElementById('storylet-start-count').textContent = `${_storyletData.starts.length}개`;
        buildStoryletLookupIndexes();
        renderStoryletStarts();
        renderStoryletPools();
        renderStoryletEditor();
        if (forceToast) showToast('Storylet CSV를 다시 읽었습니다.');
    } catch (e) {
        console.error('[ops] loadStorylets 실패:', e);
        document.getElementById('storylet-loading').textContent = 'Storylet CSV 로딩 실패 — 서버 콘솔을 확인하세요.';
        showToast('Storylet CSV 로딩 실패', true);
    }
}

function renderStoryletStarts() {
    const list = document.getElementById('storylet-start-list');
    if (!list) return;
    const query = (document.getElementById('storylet-start-search')?.value || '').trim().toLowerCase();
    const starts = [...(_storyletData?.starts ?? [])]
        .filter(row => {
            if (!query) return true;
            return [row.start_key, row.start_index, row.title_kr, row.success_text_kr, storyletSearchText(row)]
                .join(' ').toLowerCase().includes(query);
        })
        .sort((a, b) => num(a.start_index) - num(b.start_index));
    list.innerHTML = starts.map(row => {
        const active = _storyletSelected?.type === 'start' && _storyletSelected.nodeId === row.node_id ? 'active' : '';
        const area = areaTypeLabel(num(row.target_area_type));
        const object = storyletObjectLabel(row);
        const beforeText = storyletPreviewText(row);
        return `
            <button class="storylet-row ${active} w-full text-left border border-gray-800 hover:border-violet-700 rounded px-2 py-2"
                    onclick="selectStorylet('start','${escapeAttr(row.node_id)}')">
                <div class="flex items-center justify-between gap-2">
                    <span class="text-xs text-violet-300">${escapeHtml(row.start_index)}. ${escapeHtml(row.start_key)}</span>
                    <span class="text-[11px] text-gray-600">${escapeHtml(area)}</span>
                </div>
                <div class="text-[11px] text-gray-600 mt-0.5">${escapeHtml(area)} · ${escapeHtml(object)}</div>
                <div class="text-sm text-gray-100 mt-1 truncate">${escapeHtml(beforeText || '선택지 전 본문 없음')}</div>
                <div class="text-xs text-gray-500 truncate mt-0.5">선택지: ${escapeHtml(row.title_kr)}</div>
            </button>
        `;
    }).join('');
}

function renderStoryletPools() {
    if (!_storyletData) return;
    const list = document.getElementById('storylet-pool-list');
    const stageFilter = document.getElementById('storylet-stage-filter').value;
    const query = document.getElementById('storylet-search').value.trim().toLowerCase();
    const rows = [..._storyletData.pools]
        .filter(row => stageFilter === 'all' || row.stage_index === stageFilter)
        .filter(row => {
            if (!query) return true;
            return [
                row.node_id, row.pool_key, row.stage_key, row.title_kr, row.success_text_kr,
                row.required_all_tags, row.required_any_tags, row.grant_tags, row.final_tags,
                storyletSearchText(row),
            ].join(' ').toLowerCase().includes(query);
        })
        .sort((a, b) => num(a.stage_index) - num(b.stage_index) || num(a.node_id) - num(b.node_id));

    list.innerHTML = rows.map(row => {
        const active = _storyletSelected?.type === 'pool' && _storyletSelected.nodeId === row.node_id ? 'active' : '';
        const area = areaTypeLabel(num(row.target_area_type));
        const object = storyletObjectLabel(row);
        const beforeText = storyletPreviewText(row);
        return `
            <button class="storylet-row ${active} w-full text-left border border-gray-800 hover:border-violet-700 rounded px-2 py-2"
                    onclick="selectStorylet('pool','${escapeAttr(row.node_id)}')">
                <div class="flex items-center justify-between gap-2">
                    <span class="text-xs text-violet-300">S${escapeHtml(row.stage_index)} · ${escapeHtml(row.stage_key)}</span>
                    <span class="text-[11px] text-blue-300">${escapeHtml(row.storylet_type || 'route')}</span>
                </div>
                <div class="flex items-center gap-1 mt-1 text-[11px] text-gray-500">
                    <span>${escapeHtml(row.pool_key)}</span>
                    <span>·</span>
                    <span>${escapeHtml(area)}</span>
                    <span>·</span>
                    <span>${escapeHtml(object)}</span>
                </div>
                <div class="text-sm text-gray-100 mt-1 truncate">${escapeHtml(beforeText || '선택지 전 본문 없음')}</div>
                <div class="text-xs text-gray-500 truncate mt-0.5">선택지: ${escapeHtml(row.title_kr)}</div>
            </button>
        `;
    }).join('');
}

async function selectStorylet(type, nodeId) {
    // 미저장 변경이 있으면 confirm 대신 조용히 자동 저장하고 이동 (변경 손실 없음).
    if (_storyletDirty) await saveSelectedStorylet({ silent: true });

    const source = type === 'start' ? _storyletData.starts : _storyletData.pools;
    const row = source.find(item => item.node_id === nodeId);
    if (!row) return;

    _storyletSelected = { type, nodeId, row: { ...row } };
    _storyletDirty = false;
    renderStoryletStarts();
    renderStoryletPools();
    renderStoryletEditor();
}

function renderStoryletEditor() {
    const pane = document.getElementById('storylet-editor-pane');
    if (!pane) return;

    pane.innerHTML = (_storyletSelected?.type === 'start' || _storyletSelected?.type === 'pool')
        ? buildStoryletFormHtml(_storyletSelected.type, _storyletSelected.row)
        : '<div class="text-xs text-gray-600 border border-gray-800 rounded px-3 py-10 text-center">좌측에서 시작점 또는 후보를 선택하면 여기서 편집합니다.</div>';

    renderStoryletValidationBadges(pane.querySelector('[data-storylet-form]'));
}

function markStoryletDirty(event) {
    if (!_storyletSelected) return;
    // 선택지 전 본문/클릭 선택지 편집도 통합 저장에 포함되므로 모두 dirty 처리 + 자동저장 예약.
    setStoryletDirty();
    scheduleStoryletAutosave();
    renderStoryletValidationBadges(document.querySelector('[data-storylet-form]'));
}

function setStoryletDirty() {
    if (!_storyletSelected) return;
    _storyletDirty = true;
    document.querySelector(`[data-storylet-dirty="${_storyletSelected.type}"]`)?.classList.remove('hidden');
    updateStoryletSaveBar('dirty');
}

async function saveSelectedStorylet(opts = {}) {
    const silent = opts.silent === true;
    if (!_storyletSelected) return;
    if (_storyletSaving) return;

    const form = document.querySelector(`[data-storylet-form="${_storyletSelected.type}"]`);
    if (!form) return;

    _storyletSaving = true;
    updateStoryletSaveBar('saving');

    const updated = { ..._storyletSelected.row };
    for (const el of form.elements) {
        if (!el.name) continue;
        if (_storyletSelected.type === 'start' && el.name === 'stage_index') updated.start_index = el.value;
        else if (_storyletSelected.type === 'start' && el.name === 'pool_key') updated.start_key = el.value;
        else updated[el.name] = toCsvEditorText(el.name, el.value);
    }
    const interactableUpdates = collectInteractableUpdates(form);
    const objectActionUpdates = collectObjectActionUpdates(form);
    const recipeUpdates = collectRecipeUpdates(form);
    const itemUpdates = collectItemUpdates(form);

    const url = _storyletSelected.type === 'start'
        ? `/api/storylets/start/${encodeURIComponent(_storyletSelected.nodeId)}`
        : `/api/storylets/pool/${encodeURIComponent(_storyletSelected.nodeId)}`;

    try {
        const res = await fetch(url, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(updated),
        });
        const payload = await res.json();
        if (!res.ok) throw new Error(payload.message || `HTTP ${res.status}`);

        let latestData = payload.data;
        for (const update of interactableUpdates) {
            const interactablePayload = await putStoryletInteractable(update.id, update.body);
            latestData = interactablePayload.data ?? latestData;
        }
        for (const update of objectActionUpdates) {
            const actionPayload = await putStoryletObjectAction(update.groupKey, update.actionId, update.body);
            latestData = actionPayload.data ?? latestData;
        }
        for (const update of recipeUpdates) {
            const recipePayload = await putStoryletRecipe(update.id, update.body);
            latestData = recipePayload.data ?? latestData;
        }
        for (const update of itemUpdates) {
            const itemPayload = await putStoryletItem(update.id, update.body);
            latestData = itemPayload.data ?? latestData;
        }

        _storyletData = latestData;
        buildStoryletLookupIndexes();
        _storyletDirty = false;
        document.querySelector('[data-storylet-dirty]')?.classList.add('hidden');
        const source = _storyletSelected.type === 'start' ? _storyletData.starts : _storyletData.pools;
        const freshRow = source.find(row => row.node_id === _storyletSelected.nodeId);
        if (freshRow) _storyletSelected.row = { ...freshRow };
        renderStoryletStarts();
        renderStoryletPools();
        // silent(자동) 저장 시에는 폼 DOM을 다시 그리지 않아 입력 포커스를 유지한다.
        if (!silent) renderStoryletEditor();
        updateStoryletSaveBar('saved');
        if (!silent) {
            const savedParts = ['Storylet'];
            if (interactableUpdates.length > 0) savedParts.push('선택 전 본문');
            if (objectActionUpdates.length > 0) savedParts.push('클릭 선택지');
            if (recipeUpdates.length > 0) savedParts.push('조합 재료');
            if (itemUpdates.length > 0) savedParts.push('아이템 이름');
            showToast(`${savedParts.join(' + ')} 저장 완료`);
        }
    } catch (e) {
        console.error('[ops] saveSelectedStorylet 실패:', e);
        updateStoryletSaveBar('error');
        showToast(`저장 실패: ${e.message}`, true);
    } finally {
        _storyletSaving = false;
    }
}

// ── 인라인 검증 / 매직넘버 라벨 (조용한 실패 방지) ──────────────────────────
function storyletFieldValidation(name, value) {
    const v = String(value ?? '').trim();
    if (name === 'interact_id') {
        if (!v || v === '0') return null;
        return _storyletInteractablesById.has(num(v)) ? null : { level: 'warn', msg: '존재하지 않는 interact_id' };
    }
    if (name === 'action_group_key') {
        if (!v) return null;
        return _storyletActionsByGroup.has(v) ? null : { level: 'warn', msg: '매칭 object_action 없음' };
    }
    if (name === 'title_kr') {
        return v ? null : { level: 'error', msg: '선택지 제목(필수) 비어있음' };
    }
    if (name === 'target_area_type') {
        if (!v) return null;
        const label = _areaLabelsFromCsv.get(num(v)) || AREA_LABEL_OVERRIDES[num(v)] || AREA_LABELS[num(v)];
        return label ? { level: 'ok', msg: label } : { level: 'warn', msg: '미정의 area' };
    }
    if (name === 'target_object_type') {
        if (!v) return null;
        const label = _storyletObjectTypeLabels.get(num(v));
        return label ? { level: 'ok', msg: label } : null;
    }
    return null;
}

function validationBadgeHtml(name) {
    return `<span data-validate="${name}" class="block text-[11px] mt-0.5 min-h-[0.9rem]"></span>`;
}

function renderStoryletValidationBadges(form) {
    if (!form) return;
    for (const span of form.querySelectorAll('[data-validate]')) {
        const name = span.getAttribute('data-validate');
        const input = form.querySelector(`[name="${name}"]`);
        const res = storyletFieldValidation(name, input?.value);
        if (!res) { span.textContent = ''; span.className = 'block text-[11px] mt-0.5 min-h-[0.9rem]'; continue; }
        const color = res.level === 'error' ? 'text-red-400' : res.level === 'warn' ? 'text-orange-400' : 'text-green-500';
        const icon = res.level === 'error' ? '🔴' : res.level === 'warn' ? '🟠' : '🟢';
        span.textContent = `${icon} ${res.msg}`;
        span.className = `block text-[11px] mt-0.5 min-h-[0.9rem] ${color}`;
    }
}

// area/object 매직넘버 datalist (값=정수, 설명=한글 라벨)
function storyletAreaDatalistHtml() {
    const seen = new Map();
    for (const [k, v] of Object.entries(AREA_LABEL_OVERRIDES)) seen.set(num(k), v);
    for (const [k, v] of _areaLabelsFromCsv) seen.set(num(k), v);
    return [...seen.entries()].sort((a, b) => a[0] - b[0])
        .map(([k, v]) => `<option value="${k}">${escapeHtml(v)}</option>`).join('');
}
function storyletObjectDatalistHtml() {
    return [..._storyletObjectTypeLabels.entries()].sort((a, b) => a[0] - b[0])
        .map(([k, v]) => `<option value="${k}">${escapeHtml(v)}</option>`).join('');
}

// 3언어 입력 보조: 선택지 제목/결과 본문의 KR값을 빈 EN/JP 칸에 복사 (번역 아님)
function fillTriLangSection(form) {
    if (!form) return;
    let changed = false;
    for (const base of ['title', 'success_text']) {
        const kr = form.querySelector(`[name="${base}_kr"]`);
        if (!kr || !kr.value.trim()) continue;
        for (const lang of ['en', 'jp']) {
            const f = form.querySelector(`[name="${base}_${lang}"]`);
            if (f && !f.value.trim()) { f.value = kr.value; changed = true; }
        }
    }
    if (changed) { setStoryletDirty(); scheduleStoryletAutosave(); }
    showToast(changed ? 'KR값을 빈 EN/JP 칸에 복사했습니다.' : '복사할 빈 EN/JP 칸이 없습니다.');
}

// 접이식 섹션 (<details>는 닫혀도 DOM 유지 → form.elements 순회에 안전)
function storyletSection(title, open, inner) {
    return `
        <details ${open ? 'open' : ''} class="border border-gray-800 rounded bg-gray-950">
            <summary class="cursor-pointer select-none px-3 py-2 text-xs font-semibold text-gray-300 marker:text-violet-400">${escapeHtml(title)}</summary>
            <div class="px-3 pb-3 pt-1">${inner}</div>
        </details>`;
}

function buildStoryletFormHtml(type, row) {
    const stageValue = type === 'start' ? row.start_index : row.stage_index;
    const keyValue = type === 'start' ? row.start_key : row.pool_key;
    const title = type === 'start'
        ? `${row.start_index}. ${row.start_key} · ${row.title_kr}`
        : `S${row.stage_index} · ${row.title_kr}`;
    const subtitle = type === 'start'
        ? `시작 Storylet #${row.node_id} · 요구 파트 ${row.required_part_ids || '-'}`
        : `${row.pool_key} · ${row.required_all_tags || '조건 없음'} -> ${row.grant_tags || '태그 없음'}`;

    return `
        <div class="border border-violet-900 bg-gray-900 rounded p-3">
            <div class="flex items-start justify-between gap-3 mb-3">
                <div>
                    <h4 class="text-sm font-semibold text-violet-200">${escapeHtml(title)}</h4>
                    <p class="text-xs text-gray-600 mt-0.5">${escapeHtml(subtitle)}</p>
                </div>
                <span data-storylet-dirty="${type}" class="hidden shrink-0 text-xs bg-yellow-900 text-yellow-200 px-2 py-1 rounded">수정됨</span>
            </div>

            <form data-storylet-form="${type}" class="space-y-3" oninput="markStoryletDirty(event)">
                <div class="grid grid-cols-2 md:grid-cols-4 gap-2">
                    <label class="text-xs text-gray-500">node_id
                        <input name="node_id" value="${escapeAttr(row.node_id)}" readonly class="mt-1 w-full bg-gray-950 border border-gray-800 rounded px-2 py-1 text-gray-500" />
                    </label>
                    <label class="text-xs text-gray-500">순서/단계
                        <input name="stage_index" value="${escapeAttr(stageValue ?? '')}" class="mt-1 w-full bg-gray-950 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                    </label>
                    <label class="text-xs text-gray-500">key
                        <input name="pool_key" value="${escapeAttr(keyValue ?? '')}" class="mt-1 w-full bg-gray-950 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                    </label>
                </div>

                <datalist id="dl-storylet-area">${storyletAreaDatalistHtml()}</datalist>
                <datalist id="dl-storylet-object">${storyletObjectDatalistHtml()}</datalist>
                <div class="grid grid-cols-2 md:grid-cols-6 gap-2">
                    <label class="text-xs text-gray-500">area
                        <input name="target_area_type" list="dl-storylet-area" value="${escapeAttr(row.target_area_type ?? '')}" class="mt-1 w-full bg-gray-950 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                        ${validationBadgeHtml('target_area_type')}
                    </label>
                    <label class="text-xs text-gray-500">object
                        <input name="target_object_type" list="dl-storylet-object" value="${escapeAttr(row.target_object_type ?? '')}" class="mt-1 w-full bg-gray-950 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                        ${validationBadgeHtml('target_object_type')}
                    </label>
                    <label class="text-xs text-gray-500">interact_id
                        <input name="interact_id" value="${escapeAttr(row.interact_id ?? '')}" class="mt-1 w-full bg-gray-950 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                        ${validationBadgeHtml('interact_id')}
                    </label>
                    <label class="text-xs text-gray-500">action_group
                        <input name="action_group_key" value="${escapeAttr(row.action_group_key ?? '')}" class="mt-1 w-full bg-gray-950 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                        ${validationBadgeHtml('action_group_key')}
                    </label>
                    <label class="text-xs text-gray-500">reward
                        <input name="reward_kind" value="${escapeAttr(row.reward_kind ?? '')}" class="mt-1 w-full bg-gray-950 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                    </label>
                </div>

                ${storyletSection('선택지 전 본문 (interactable)', true, buildStoryletObjectContextHtml(row))}

                <div class="border-t border-gray-800 pt-3">
                    <div class="flex items-center justify-between mb-2">
                        <h5 class="text-xs font-semibold text-gray-400">클릭 후 Storylet 선택지/결과</h5>
                        <button type="button" onclick="fillTriLangSection(this.closest('form'))"
                                class="text-[11px] bg-gray-800 hover:bg-gray-700 border border-gray-700 text-gray-300 px-2 py-1 rounded">KR값 복사 ⮐</button>
                    </div>
                    <label class="storylet-field block text-xs text-gray-500">선택지 제목 KR
                        <input name="title_kr" value="${escapeAttr(row.title_kr ?? '')}" class="mt-1 w-full bg-gray-950 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                        ${validationBadgeHtml('title_kr')}
                    </label>
                    <label class="storylet-field block text-xs text-gray-500 mt-2">성공/결과 본문 KR
                        <textarea name="success_text_kr" class="mt-1 w-full bg-gray-950 border border-gray-700 rounded px-2 py-1 text-gray-100 leading-relaxed">${escapeEditorText(row.success_text_kr)}</textarea>
                    </label>

                    <div class="grid grid-cols-1 md:grid-cols-2 gap-2 mt-2">
                        <label class="storylet-field text-xs text-gray-500">선택지 제목 EN
                            <input name="title_en" value="${escapeAttr(row.title_en ?? '')}" class="mt-1 w-full bg-gray-950 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                        </label>
                        <label class="storylet-field text-xs text-gray-500">선택지 제목 JP
                            <input name="title_jp" value="${escapeAttr(row.title_jp ?? '')}" class="mt-1 w-full bg-gray-950 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                        </label>
                    </div>
                    <div class="grid grid-cols-1 md:grid-cols-2 gap-2 mt-2">
                        <label class="storylet-field text-xs text-gray-500">결과 본문 EN
                            <textarea name="success_text_en" class="mt-1 w-full bg-gray-950 border border-gray-700 rounded px-2 py-1 text-gray-100">${escapeEditorText(row.success_text_en)}</textarea>
                        </label>
                        <label class="storylet-field text-xs text-gray-500">결과 본문 JP
                            <textarea name="success_text_jp" class="mt-1 w-full bg-gray-950 border border-gray-700 rounded px-2 py-1 text-gray-100">${escapeEditorText(row.success_text_jp)}</textarea>
                        </label>
                    </div>
                </div>

                ${storyletSection('클릭 가능 선택지 (object_action)', false, buildStoryletActionFlowHtml(type, row))}
                ${storyletSection('진행 조건 태그', false, buildStoryletTagEditorHtml(type, row))}
                ${storyletSection('해금 아이템 / 조합 레시피', false, buildStoryletUnlockItemHtml(type, row))}

                <div class="flex items-center justify-end gap-2 pt-1">
                    <span class="text-[11px] text-gray-600">입력하면 전체가 함께 자동 저장 · Ctrl+S</span>
                    <button type="button" onclick="flushStoryletAutosave()" class="bg-violet-700 hover:bg-violet-600 text-white text-xs px-3 py-1.5 rounded">
                        전체 저장
                    </button>
                </div>
            </form>

            ${buildStoryletNextPreviewHtml({ type, nodeId: row.node_id, row })}
        </div>
    `;
}

function buildStoryletNextPreviewHtml(selected) {
    if (!selected) return '';

    const ownedTags = selected.type === 'start'
        ? [`stage_1`, `start_${(selected.row.start_key || '').toLowerCase()}`]
        : splitTags(selected.row.grant_tags);

    const candidates = (_storyletData?.pools ?? [])
        .filter(row => row.node_id !== selected.nodeId && isStoryletAvailable(row, ownedTags))
        .sort((a, b) => num(a.stage_index) - num(b.stage_index) || num(a.node_id) - num(b.node_id));

    if (ownedTags.length === 0) {
        return '<div class="mt-4 border-t border-gray-800 pt-3 text-xs text-gray-600">grant_tags가 없어 다음 후보를 계산할 수 없습니다.</div>';
    }
    if (candidates.length === 0) {
        return '<div class="mt-4 border-t border-gray-800 pt-3 text-xs text-gray-600">현재 태그로 바로 열리는 다음 후보가 없습니다.</div>';
    }

    const rowsHtml = candidates.slice(0, 12).map(row => `
            <button class="w-full text-left storylet-tag rounded px-2 py-1.5 hover:border-violet-600"
                    onclick="selectStorylet('pool','${escapeAttr(row.node_id)}')">
                <div class="flex items-center justify-between gap-2">
                    <span class="text-xs text-gray-300">S${escapeHtml(row.stage_index)} · ${escapeHtml(storyletObjectLabel(row))}</span>
                    <span class="text-[11px] text-gray-500">${escapeHtml(row.pool_key)}</span>
                </div>
                <div class="text-xs text-gray-200 truncate mt-0.5">${escapeHtml(storyletPreviewText(row) || '선택지 전 본문 없음')}</div>
                <div class="text-[11px] text-gray-500 truncate mt-0.5">선택지: ${escapeHtml(row.title_kr)} · ${escapeHtml(row.required_all_tags || row.required_any_tags || '조건 없음')}</div>
            </button>
    `).join('');

    return `
        <div class="mt-4 border-t border-gray-800 pt-3">
            <h4 class="text-xs font-semibold text-gray-500 uppercase tracking-wider mb-2">연결 후보 미리보기</h4>
            <div class="space-y-1">${rowsHtml}</div>
        </div>
    `;
}

function buildStoryletTagEditorHtml(type, row) {
    if (type === 'start') {
        const startTag = `start_${String(row.start_key || '').trim().toLowerCase()}`;
        const generatedTags = ['record_case', startTag, 'stage_1'].filter(tag => tag && tag !== 'start_');
        return `
            <div class="border-t border-gray-800 pt-3 text-xs">
                <h5 class="font-semibold text-gray-400 mb-2">진행 태그</h5>
                <div class="bg-gray-950 border border-gray-800 rounded px-3 py-2">
                    <div class="text-gray-500 mb-2">1단계 시작점은 required_all_tags를 직접 입력하지 않습니다. 완료 시 아래 태그가 자동 지급됩니다.</div>
                    <div class="flex flex-wrap gap-1">
                        ${generatedTags.map(tag => `<span class="storylet-tag rounded px-2 py-1 text-[11px]">${escapeHtml(tag)}</span>`).join('')}
                    </div>
                </div>
            </div>
        `;
    }

    const knownTags = knownStoryletTags();
    const chipHtml = knownTags.slice(0, 80).map(tag => `
        <button type="button"
                onclick="appendStoryletTagToFocusedField(${escapeAttr(JSON.stringify(tag))})"
                class="storylet-tag rounded px-2 py-1 text-[11px] hover:border-violet-600">
            ${escapeHtml(tag)}
        </button>
    `).join('');

    return `
        <div class="border-t border-gray-800 pt-3">
            <div class="flex items-center justify-between gap-2 mb-2">
                <h5 class="text-xs font-semibold text-gray-400">진행 조건 태그</h5>
                <span class="text-[11px] text-gray-600">서버 판정 필드 · | 로 구분</span>
            </div>
            <div class="grid grid-cols-1 md:grid-cols-2 gap-2">
                <label class="text-xs text-gray-500">required_all_tags
                    <textarea name="required_all_tags" data-storylet-tag-field class="mt-1 w-full bg-gray-950 border border-gray-700 rounded px-2 py-1 text-gray-100">${escapeHtml(row.required_all_tags ?? '')}</textarea>
                </label>
                <label class="text-xs text-gray-500">required_any_tags
                    <textarea name="required_any_tags" data-storylet-tag-field class="mt-1 w-full bg-gray-950 border border-gray-700 rounded px-2 py-1 text-gray-100">${escapeHtml(row.required_any_tags ?? '')}</textarea>
                </label>
            </div>
            <div class="grid grid-cols-1 md:grid-cols-3 gap-2 mt-2">
                <label class="text-xs text-gray-500">blocked_tags
                    <textarea name="blocked_tags" data-storylet-tag-field class="mt-1 w-full bg-gray-950 border border-gray-700 rounded px-2 py-1 text-gray-100">${escapeHtml(row.blocked_tags ?? '')}</textarea>
                </label>
                <label class="text-xs text-gray-500">grant_tags
                    <textarea name="grant_tags" data-storylet-tag-field class="mt-1 w-full bg-gray-950 border border-gray-700 rounded px-2 py-1 text-gray-100">${escapeHtml(row.grant_tags ?? '')}</textarea>
                </label>
                <label class="text-xs text-gray-500">final_tags
                    <textarea name="final_tags" data-storylet-tag-field class="mt-1 w-full bg-gray-950 border border-gray-700 rounded px-2 py-1 text-gray-100">${escapeHtml(row.final_tags ?? '')}</textarea>
                </label>
            </div>
            <div class="text-[11px] text-gray-600 mt-2 mb-1">태그 입력칸을 클릭한 뒤 아래 태그를 누르면 중복 없이 추가됩니다.</div>
            <div class="flex flex-wrap gap-1 max-h-28 overflow-y-auto pr-1">${chipHtml}</div>
        </div>
    `;
}

function knownStoryletTags() {
    const tags = new Set(['record_case', 'stage_1']);
    for (const row of _storyletData?.starts ?? []) {
        const startTag = `start_${String(row.start_key || '').trim().toLowerCase()}`;
        if (startTag !== 'start_') tags.add(startTag);
    }
    for (const row of _storyletData?.pools ?? []) {
        for (const field of ['required_all_tags', 'required_any_tags', 'blocked_tags', 'grant_tags', 'final_tags']) {
            for (const tag of splitTags(row[field])) tags.add(tag);
        }
    }
    return [...tags].sort((a, b) => a.localeCompare(b));
}

function appendStoryletTagToFocusedField(tag) {
    const form = _storyletSelected
        ? document.querySelector(`[data-storylet-form="${_storyletSelected.type}"]`)
        : null;
    const active = document.activeElement?.matches?.('[data-storylet-tag-field]')
        ? document.activeElement
        : form?.querySelector('[name="required_all_tags"]');
    if (!active) return;

    const tags = splitTags(active.value);
    if (!tags.includes(tag)) {
        tags.push(tag);
        active.value = tags.join('|');
        active.dispatchEvent(new Event('input', { bubbles: true }));
    }
    active.focus();
}

async function saveStoryletInteractable(interactId) {
    if (_storyletDirty && !confirm('Storylet 수정사항이 아직 저장되지 않았습니다. 오브젝트 본문을 저장하면서 Storylet 편집값을 버릴까요?')) return;

    const container = document.querySelector(`[data-interactable-editor="${interactId}"]`);
    if (!container) return;

    const body = collectInteractableUpdate(container);

    try {
        const payload = await putStoryletInteractable(interactId, body);
        _storyletData = payload.data;
        buildStoryletLookupIndexes();
        if (_storyletSelected) {
            const source = _storyletSelected.type === 'start' ? _storyletData.starts : _storyletData.pools;
            const freshRow = source.find(row => row.node_id === _storyletSelected.nodeId);
            if (freshRow) _storyletSelected.row = { ...freshRow };
        }
        renderStoryletStarts();
        renderStoryletPools();
        renderStoryletEditor();
        showToast('선택지 전 본문 저장 완료');
    } catch (e) {
        console.error('[ops] saveStoryletInteractable 실패:', e);
        showToast(`오브젝트 본문 저장 실패: ${e.message}`, true);
    }
}

function collectInteractableUpdates(root) {
    return [...root.querySelectorAll('[data-interactable-editor]')]
        .map(container => ({
            id: container.dataset.interactableEditor,
            body: collectInteractableUpdate(container),
        }))
        .filter(update => update.id);
}

function collectInteractableUpdate(container) {
    const body = {};
    for (const el of container.querySelectorAll('[data-interactable-field]')) {
        const field = el.dataset.interactableField;
        body[field] = toCsvEditorText(field, el.value);
    }
    return body;
}

async function putStoryletInteractable(interactId, body) {
    const res = await fetch(`/api/storylets/interactable/${encodeURIComponent(interactId)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
    });
    const payload = await res.json();
    if (!res.ok) throw new Error(payload.message || `HTTP ${res.status}`);
    return payload;
}

async function saveStoryletObjectAction(actionGroupKey, actionId) {
    if (_storyletDirty && !confirm('Storylet 수정사항이 아직 저장되지 않았습니다. 클릭 선택지를 저장하면서 Storylet 편집값을 버릴까요?')) return;

    const container = [...document.querySelectorAll('[data-object-action-editor]')]
        .find(el => el.dataset.objectActionGroup === actionGroupKey && el.dataset.objectActionId === actionId);
    if (!container) return;

    try {
        const payload = await putStoryletObjectAction(actionGroupKey, actionId, collectObjectActionUpdate(container));
        _storyletData = payload.data;
        buildStoryletLookupIndexes();
        _storyletDirty = false;
        if (_storyletSelected) {
            const source = _storyletSelected.type === 'start' ? _storyletData.starts : _storyletData.pools;
            const freshRow = source.find(row => row.node_id === _storyletSelected.nodeId);
            if (freshRow) _storyletSelected.row = { ...freshRow };
        }
        renderStoryletStarts();
        renderStoryletPools();
        renderStoryletEditor();
        showToast('클릭 선택지 저장 완료');
    } catch (e) {
        console.error('[ops] saveStoryletObjectAction failed:', e);
        showToast(`클릭 선택지 저장 실패: ${e.message}`, true);
    }
}

function collectObjectActionUpdates(root) {
    return [...root.querySelectorAll('[data-object-action-editor]')]
        .map(container => ({
            groupKey: container.dataset.objectActionGroup,
            actionId: container.dataset.objectActionId,
            body: collectObjectActionUpdate(container),
        }))
        .filter(update => update.groupKey && update.actionId);
}

function collectObjectActionUpdate(container) {
    const body = {};
    for (const el of container.querySelectorAll('[data-object-action-field]')) {
        const field = el.dataset.objectActionField;
        body[field] = toCsvEditorText(field, el.value);
    }
    return body;
}

async function putStoryletObjectAction(actionGroupKey, actionId, body) {
    const res = await fetch(`/api/storylets/object-action/${encodeURIComponent(actionGroupKey)}/${encodeURIComponent(actionId)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
    });
    const payload = await res.json();
    if (!res.ok) throw new Error(payload.message || `HTTP ${res.status}`);
    return payload;
}

function collectRecipeUpdates(root) {
    return [...root.querySelectorAll('[data-recipe-editor]')]
        .map(container => ({
            id: container.dataset.recipeEditor,
            body: collectRecipeUpdate(container),
        }))
        .filter(update => update.id);
}

function collectRecipeUpdate(container) {
    const body = {};
    for (const el of container.querySelectorAll('[data-recipe-field]')) {
        const field = el.dataset.recipeField;
        body[field] = toCsvEditorText(field, el.value);
    }
    return body;
}

async function putStoryletRecipe(recipeId, body) {
    const res = await fetch(`/api/storylets/recipe/${encodeURIComponent(recipeId)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
    });
    const payload = await res.json();
    if (!res.ok) throw new Error(payload.message || `HTTP ${res.status}`);
    return payload;
}

function collectItemUpdates(root) {
    const updates = new Map();
    for (const container of root.querySelectorAll('[data-item-editor]')) {
        const id = container.dataset.itemEditor;
        if (!id) continue;
        updates.set(id, {
            id,
            body: collectItemUpdate(container),
        });
    }
    return [...updates.values()];
}

function collectItemUpdate(container) {
    const body = {};
    for (const el of container.querySelectorAll('[data-item-field]')) {
        const field = el.dataset.itemField;
        body[field] = toCsvEditorText(field, el.value);
    }
    return body;
}

async function putStoryletItem(id, body) {
    const res = await fetch(`/api/storylets/item/${encodeURIComponent(id)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
    });
    const payload = await res.json();
    if (!res.ok) throw new Error(payload.message || `HTTP ${res.status}`);
    return payload;
}

function buildStoryletActionFlowHtmlReadOnly(type, row) {
    const objectType = num(row.target_object_type);
    const groupKey = row.action_group_key || defaultActionGroupKey(objectType);
    const actions = groupKey
        ? (_storyletActionsByGroup.get(groupKey) ?? [])
        : (_storyletActionsByObject.get(objectType) ?? []);
    const sourceLabel = type === 'start'
        ? 'mission_storylet_start.csv'
        : 'mission_storylet_pool.csv';

    const actionHtml = actions.length > 0
        ? actions.slice(0, 8).map(action => `
            <div class="storylet-tag rounded px-2 py-2">
                <div class="flex flex-wrap items-center justify-between gap-2">
                    <span class="text-xs text-gray-100">${escapeHtml(action.action_text_kr || `action ${action.action_id}`)}</span>
                    <span class="text-[11px] text-gray-500">action_id ${escapeHtml(action.action_id ?? '')}</span>
                </div>
                <div class="text-[11px] text-gray-400 mt-1">${escapeHtml(action.result_text_kr || '결과 본문 없음')}</div>
                <div class="text-[11px] text-gray-600 mt-1">
                    result ${escapeHtml(action.result_type ?? '-')} / ${escapeHtml(action.result_id ?? '-')}
                    · stamina ${escapeHtml(action.stamina_cost ?? '0')}
                    · require_item ${escapeHtml(action.require_item_id ?? '0')}
                </div>
            </div>
        `).join('')
        : '<div class="text-xs text-gray-600 border border-gray-800 rounded px-2 py-2">연결된 object_action 행이 없습니다.</div>';

    return `
        <div class="border-t border-gray-800 pt-3">
            <div class="flex items-center justify-between gap-2 mb-2">
                <h5 class="text-xs font-semibold text-gray-400">Storylet 이후 클릭 가능 선택지</h5>
                <span class="text-[11px] text-gray-600">${escapeHtml(groupKey || `object_type ${objectType}`)}</span>
            </div>
            <p class="text-[11px] text-gray-600 mb-2">${sourceLabel} 실행 뒤 action_group_key가 같은 object_action이 표시되는 흐름입니다.</p>
            <div class="space-y-1">${actionHtml}</div>
        </div>
    `;
}

function buildStoryletActionFlowHtml(type, row) {
    const objectType = num(row.target_object_type);
    const groupKey = row.action_group_key || defaultActionGroupKey(objectType);
    const actions = groupKey
        ? (_storyletActionsByGroup.get(groupKey) ?? [])
        : (_storyletActionsByObject.get(objectType) ?? []);
    const sourceLabel = type === 'start'
        ? 'mission_storylet_start.csv'
        : 'mission_storylet_pool.csv';

    const actionHtml = actions.length > 0
        ? actions.slice(0, 8).map(action => {
            const currentGroupKey = action.action_group_key || groupKey || defaultActionGroupKey(action.object_type);
            const currentActionId = action.action_id ?? '';
            return `
                <div data-object-action-editor
                     data-object-action-group="${escapeAttr(currentGroupKey)}"
                     data-object-action-id="${escapeAttr(currentActionId)}"
                     class="storylet-tag rounded px-2 py-2">
                    <div class="flex flex-wrap items-center justify-between gap-2 mb-2">
                        <div>
                            <div class="text-xs text-gray-100">${escapeHtml(action.action_text_kr || `action ${currentActionId}`)}</div>
                            <div class="text-[11px] text-gray-600">object_action.csv · action_id ${escapeHtml(currentActionId)}</div>
                        </div>
                    </div>
                    <div class="grid grid-cols-2 md:grid-cols-6 gap-2">
                        <label class="text-[11px] text-gray-500">object
                            <input data-object-action-field="object_type" value="${escapeAttr(action.object_type ?? '')}"
                                   class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                        </label>
                        <label class="text-[11px] text-gray-500">action_group
                            <input data-object-action-field="action_group_key" value="${escapeAttr(currentGroupKey)}"
                                   class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                        </label>
                        <label class="text-[11px] text-gray-500">action_id
                            <input data-object-action-field="action_id" value="${escapeAttr(currentActionId)}"
                                   class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                        </label>
                        <label class="text-[11px] text-gray-500">state
                            <input data-object-action-field="state" value="${escapeAttr(action.state ?? '')}"
                                   class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                        </label>
                        <label class="text-[11px] text-gray-500">stamina
                            <input data-object-action-field="stamina_cost" value="${escapeAttr(action.stamina_cost ?? '')}"
                                   class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                        </label>
                        <label class="text-[11px] text-gray-500">require_item
                            <input data-object-action-field="require_item_id" value="${escapeAttr(action.require_item_id ?? '')}"
                                   class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                        </label>
                    </div>
                    <label class="block text-[11px] text-violet-300 mt-2">클릭 선택지 문구 KR
                        <input data-object-action-field="action_text_kr" value="${escapeAttr(action.action_text_kr ?? '')}"
                               class="mt-1 w-full bg-gray-900 border border-violet-900 rounded px-2 py-1 text-gray-100" />
                    </label>
                    <label class="block text-[11px] text-violet-300 mt-2">클릭 결과 본문 KR
                        <textarea data-object-action-field="result_text_kr"
                                  class="mt-1 w-full bg-gray-900 border border-violet-900 rounded px-2 py-1 text-gray-100 leading-relaxed"
                                  style="min-height: 4.5rem;">${escapeEditorText(action.result_text_kr)}</textarea>
                    </label>
                    <div class="grid grid-cols-1 md:grid-cols-2 gap-2 mt-2">
                        <label class="text-[11px] text-gray-500">선택지 EN
                            <input data-object-action-field="action_text_en" value="${escapeAttr(action.action_text_en ?? '')}"
                                   class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                        </label>
                        <label class="text-[11px] text-gray-500">선택지 JP
                            <input data-object-action-field="action_text_jp" value="${escapeAttr(action.action_text_jp ?? '')}"
                                   class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                        </label>
                        <label class="text-[11px] text-gray-500">결과 EN
                            <textarea data-object-action-field="result_text_en"
                                      class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100"
                                      style="min-height: 3.5rem;">${escapeEditorText(action.result_text_en)}</textarea>
                        </label>
                        <label class="text-[11px] text-gray-500">결과 JP
                            <textarea data-object-action-field="result_text_jp"
                                      class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100"
                                      style="min-height: 3.5rem;">${escapeEditorText(action.result_text_jp)}</textarea>
                        </label>
                    </div>
                    <div class="grid grid-cols-2 md:grid-cols-5 gap-2 mt-2">
                        <label class="text-[11px] text-gray-500">result_type
                            <input data-object-action-field="result_type" value="${escapeAttr(action.result_type ?? '')}"
                                   class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                        </label>
                        <label class="text-[11px] text-gray-500">result_id
                            <input data-object-action-field="result_id" value="${escapeAttr(action.result_id ?? '')}"
                                   class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                        </label>
                        <label class="text-[11px] text-gray-500">amount
                            <input data-object-action-field="result_amount" value="${escapeAttr(action.result_amount ?? '')}"
                                   class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                        </label>
                        <label class="text-[11px] text-gray-500">require_action
                            <input data-object-action-field="require_action" value="${escapeAttr(action.require_action ?? '')}"
                                   class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                        </label>
                    </div>
                </div>
            `;
        }).join('')
        : '<div class="text-xs text-gray-600 border border-gray-800 rounded px-2 py-2">연결된 object_action 행이 없습니다.</div>';

    return `
        <div class="border-t border-gray-800 pt-3">
            <div class="flex items-center justify-between gap-2 mb-2">
                <h5 class="text-xs font-semibold text-gray-400">Storylet 이후 클릭 가능 선택지</h5>
                <span class="text-[11px] text-gray-600">${escapeHtml(groupKey || `object_type ${objectType}`)}</span>
            </div>
            <p class="text-[11px] text-gray-600 mb-2">${sourceLabel} 실행 뒤 action_group_key가 같은 object_action을 편집합니다.</p>
            <div class="space-y-2">${actionHtml}</div>
        </div>
    `;
}

function buildStoryletUnlockItemHtml(type, row) {
    if (type !== 'start') {
        return `
            <div class="bg-gray-950 border border-gray-800 rounded px-3 py-2 text-xs text-gray-500">
                2~7단계 후보는 아이템 조합이 아니라 태그 조건으로 해금됩니다.
            </div>
        `;
    }

    const requiredPartIds = row.required_part_ids ?? '';
    const requiredOutputItemId = row.required_output_item_id ?? '';
    const recipeId = row.recipe_id ?? '';
    const recipe = storyletRecipeForRow(row);
    const outputPartId = num(requiredOutputItemId) || parseIdList(requiredPartIds)[0] || 0;
    const recipeInputIds = parseIdList(recipe?.input_part_ids);

    const recipeEditorHtml = recipe
        ? `
            <div data-recipe-editor="${escapeAttr(recipe.recipe_id)}" class="border-t border-gray-800 mt-3 pt-3">
                <div class="flex items-center justify-between gap-2 mb-2">
                    <span class="text-xs font-semibold text-gray-300">조합 레시피</span>
                    <span class="text-[11px] text-gray-600">mission_graph_recipe.csv #${escapeHtml(recipe.recipe_id)}</span>
                </div>
                <div class="grid grid-cols-1 md:grid-cols-3 gap-2">
                    <label class="text-[11px] text-gray-500">recipe_id
                        <input data-recipe-field="recipe_id" value="${escapeAttr(recipe.recipe_id ?? '')}" readonly
                               class="mt-1 w-full bg-gray-900 border border-gray-800 rounded px-2 py-1 text-gray-500" />
                    </label>
                    <label class="text-[11px] text-gray-500">레시피명 KR
                        <input data-recipe-field="title_kr" value="${escapeAttr(recipe.title_kr ?? '')}"
                               class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                    </label>
                    <label class="text-[11px] text-gray-500">stamina
                        <input data-recipe-field="stamina_cost" value="${escapeAttr(recipe.stamina_cost ?? '')}"
                               class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                    </label>
                </div>
                <input type="hidden" data-recipe-field="input_part_ids" value="${escapeAttr(recipe.input_part_ids ?? '')}" />
                <input type="hidden" data-recipe-field="output_part_id" value="${escapeAttr(recipe.output_part_id ?? '')}" />
                <div class="mt-2">
                    <div class="text-[11px] text-gray-600 mb-1">현재 조합 재료</div>
                    <div data-recipe-input-summary class="grid grid-cols-1 md:grid-cols-2 gap-2">
                        ${buildStoryletInputItemEditors(recipeInputIds)}
                    </div>
                </div>
            </div>
        `
        : '<div class="border-t border-gray-800 mt-3 pt-3 text-[11px] text-gray-600">연결된 recipe_id를 찾을 수 없습니다.</div>';

    return `
        <div class="bg-gray-950 border border-gray-800 rounded px-3 py-2 text-xs text-gray-400">
            <div class="flex items-center justify-between gap-2 mb-2">
                <span class="font-semibold text-violet-300">선택지 해금 아이템</span>
                <span data-unlock-output-label class="text-gray-600">${escapeHtml(outputPartId ? partLabel(outputPartId) : '아이템 없음')}</span>
            </div>
            <input type="hidden" name="required_part_ids" value="${escapeAttr(requiredPartIds)}" />
            <input type="hidden" name="required_output_item_id" value="${escapeAttr(requiredOutputItemId)}" />
            <div class="grid grid-cols-1 md:grid-cols-2 gap-2">
                <div data-unlock-output-summary>
                    ${buildStoryletSelectedItemEditor(outputPartId, '해금 아이템', '선택된 해금 아이템이 없습니다.', { removable: false })}
                </div>
                <label class="text-[11px] text-gray-500">recipe_id
                    <input name="recipe_id" value="${escapeAttr(recipeId)}"
                           class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                </label>
            </div>
            ${recipeEditorHtml}
            ${buildStoryletItemPickerHtml(outputPartId, recipeInputIds)}
        </div>
    `;
}

function buildStoryletSelectedItemEditor(partId, label, emptyText, options = {}) {
    const item = storyletItemByPartId(partId);
    if (!item) {
        return `
            <div class="border border-dashed border-gray-800 rounded px-3 py-3 text-[11px] text-gray-600">
                ${escapeHtml(emptyText)}
            </div>
        `;
    }

    const removeButton = options.removable
        ? `<button type="button" onclick="removeStoryletRecipeInput(this, '${escapeAttr(partId)}')"
                   class="bg-gray-800 hover:bg-gray-700 border border-gray-700 text-gray-300 text-[11px] px-2 py-1 rounded">재료 제거</button>`
        : '';

    return `
        <div data-item-editor="${escapeAttr(item.id)}" class="border border-gray-800 rounded p-2 bg-gray-900">
            <div class="flex items-start gap-2">
                ${buildStoryletItemIconHtml(item, 'w-12 h-12')}
                <div class="min-w-0 flex-1">
                    <div class="flex items-center justify-between gap-2">
                        <div>
                            <div class="text-[11px] text-violet-300">${escapeHtml(label)}</div>
                            <div class="text-xs text-gray-100 truncate">${escapeHtml(itemLabel(item))}</div>
                            <div class="text-[10px] text-gray-600">item ${escapeHtml(item.id)} · part ${escapeHtml(partId)}</div>
                        </div>
                        ${removeButton}
                    </div>
                    <div class="grid grid-cols-1 md:grid-cols-3 gap-1 mt-2">
                        <input data-item-field="name_kr" value="${escapeAttr(item.name_kr ?? '')}" placeholder="KR"
                               class="w-full bg-gray-950 border border-gray-700 rounded px-2 py-1 text-[11px] text-gray-100" />
                        <input data-item-field="name_en" value="${escapeAttr(item.name_en ?? '')}" placeholder="EN"
                               class="w-full bg-gray-950 border border-gray-700 rounded px-2 py-1 text-[11px] text-gray-100" />
                        <input data-item-field="name_jp" value="${escapeAttr(item.name_jp ?? '')}" placeholder="JP"
                               class="w-full bg-gray-950 border border-gray-700 rounded px-2 py-1 text-[11px] text-gray-100" />
                    </div>
                </div>
            </div>
        </div>
    `;
}

function buildStoryletInputItemEditors(partIds) {
    const uniqueIds = [...new Set(partIds.map(num).filter(id => id > 0))];
    if (uniqueIds.length === 0) {
        return '<div class="text-[11px] text-gray-600 border border-dashed border-gray-800 rounded px-3 py-3">선택된 조합 재료가 없습니다.</div>';
    }

    return uniqueIds
        .map(partId => buildStoryletSelectedItemEditor(partId, '조합 재료', '재료 없음', { removable: true }))
        .join('');
}

function buildStoryletItemPickerHtml(outputPartId, inputPartIds) {
    const selectedInputs = new Set(inputPartIds.map(num));
    const items = storyletItemCandidates();
    const cards = items.map(item => {
        const partId = partIdFromItemId(item.id);
        const canUseInRecipe = partId > 0;
        const isOutput = partId === num(outputPartId);
        const isInput = selectedInputs.has(partId);
        return `
            <div data-storylet-item-card
                 data-item-id="${escapeAttr(item.id)}"
                 data-part-id="${escapeAttr(partId)}"
                 data-item-search="${escapeAttr([item.id, item.name_kr, item.name_en, item.name_jp, item.comment_kr].join(' ').toLowerCase())}"
                 class="storylet-item-card border ${isOutput || isInput ? 'border-violet-700' : 'border-gray-800'} rounded p-2 bg-gray-900">
                <div class="flex items-start gap-2">
                    ${buildStoryletItemIconHtml(item, 'w-10 h-10')}
                    <div class="min-w-0 flex-1">
                        <div class="text-xs text-gray-100 truncate">${escapeHtml(itemLabel(item))}</div>
                        <div class="text-[10px] text-gray-600">item ${escapeHtml(item.id)} · part ${escapeHtml(partId || '-')}</div>
                    </div>
                </div>
                <div class="grid grid-cols-2 gap-1 mt-2">
                    <button type="button" data-output-button ${canUseInRecipe ? `onclick="chooseStoryletUnlockItem(this, '${escapeAttr(item.id)}')"` : 'disabled'}
                            class="${canUseInRecipe ? (isOutput ? 'bg-violet-700 text-white' : 'bg-gray-800 text-gray-300 hover:bg-gray-700') : 'bg-gray-950 text-gray-700 cursor-not-allowed'} text-[11px] px-2 py-1 rounded">
                        해금
                    </button>
                    <button type="button" data-material-button ${canUseInRecipe ? `onclick="toggleStoryletRecipeInput(this, '${escapeAttr(item.id)}')"` : 'disabled'}
                            class="${canUseInRecipe ? (isInput ? 'bg-blue-700 text-white' : 'bg-gray-800 text-gray-300 hover:bg-gray-700') : 'bg-gray-950 text-gray-700 cursor-not-allowed'} text-[11px] px-2 py-1 rounded">
                        ${canUseInRecipe ? (isInput ? '재료 해제' : '재료') : '선택 불가'}
                    </button>
                </div>
            </div>
        `;
    }).join('');

    return `
        <div class="border-t border-gray-800 mt-3 pt-3">
            <div class="flex flex-wrap items-center justify-between gap-2 mb-2">
                <span class="text-xs font-semibold text-gray-300">전체 아이템 리스트</span>
                <input data-item-filter oninput="filterStoryletItemList(this)" placeholder="아이템 이름 또는 ID 검색"
                       class="bg-gray-900 border border-gray-700 rounded px-2 py-1 text-[11px] text-gray-100 w-full md:w-64" />
            </div>
            <div data-item-picker class="grid grid-cols-1 md:grid-cols-2 gap-2 overflow-y-auto pr-1" style="max-height: 40vh;">
                ${cards}
            </div>
        </div>
    `;
}

function chooseStoryletUnlockItem(button, itemId) {
    const root = button.closest('[data-storylet-form]');
    if (!root) return;

    applyStoryletItemEditorDrafts(root);
    const partId = partIdFromItemId(itemId);
    root.querySelector('input[name="required_output_item_id"]').value = partId ? String(partId) : '';
    root.querySelector('input[name="required_part_ids"]').value = partId ? formatPartIdList([partId]) : '[]';

    const recipeOutput = root.querySelector('[data-recipe-field="output_part_id"]');
    if (recipeOutput) recipeOutput.value = partId ? String(partId) : '';

    refreshStoryletUnlockItemDisplay(root);
    setStoryletDirty();
}

function toggleStoryletRecipeInput(button, itemId) {
    const root = button.closest('[data-storylet-form]');
    if (!root) return;

    applyStoryletItemEditorDrafts(root);
    const partId = partIdFromItemId(itemId);
    if (!partId) return;

    const input = root.querySelector('[data-recipe-field="input_part_ids"]');
    if (!input) return;

    const ids = parseIdList(input.value);
    const nextIds = ids.includes(partId)
        ? ids.filter(id => id !== partId)
        : [...ids, partId];
    input.value = formatPartIdList(nextIds);

    refreshStoryletUnlockItemDisplay(root);
    setStoryletDirty();
}

function removeStoryletRecipeInput(button, partId) {
    const root = button.closest('[data-storylet-form]');
    if (!root) return;

    applyStoryletItemEditorDrafts(root);
    const input = root.querySelector('[data-recipe-field="input_part_ids"]');
    if (!input) return;

    const removeId = num(partId);
    input.value = formatPartIdList(parseIdList(input.value).filter(id => id !== removeId));

    refreshStoryletUnlockItemDisplay(root);
    setStoryletDirty();
}

function refreshStoryletUnlockItemDisplay(root) {
    const outputPartId = num(root.querySelector('input[name="required_output_item_id"]')?.value);
    const inputPartIds = parseIdList(root.querySelector('[data-recipe-field="input_part_ids"]')?.value);
    const outputSummary = root.querySelector('[data-unlock-output-summary]');
    const inputSummary = root.querySelector('[data-recipe-input-summary]');
    const outputLabel = root.querySelector('[data-unlock-output-label]');

    if (outputSummary) {
        outputSummary.innerHTML = buildStoryletSelectedItemEditor(outputPartId, '해금 아이템', '선택된 해금 아이템이 없습니다.', { removable: false });
    }
    if (inputSummary) {
        inputSummary.innerHTML = buildStoryletInputItemEditors(inputPartIds);
    }
    if (outputLabel) {
        outputLabel.textContent = outputPartId ? partLabel(outputPartId) : '아이템 없음';
    }

    syncStoryletItemPickerState(root);
}

function syncStoryletItemPickerState(root) {
    const outputPartId = num(root.querySelector('input[name="required_output_item_id"]')?.value);
    const inputPartIds = new Set(parseIdList(root.querySelector('[data-recipe-field="input_part_ids"]')?.value));

    for (const card of root.querySelectorAll('[data-storylet-item-card]')) {
        const partId = num(card.dataset.partId);
        const canUseInRecipe = partId > 0;
        const isOutput = partId === outputPartId;
        const isInput = inputPartIds.has(partId);
        card.classList.toggle('border-violet-700', isOutput || isInput);
        card.classList.toggle('border-gray-800', !(isOutput || isInput));

        const outputButton = card.querySelector('[data-output-button]');
        if (outputButton) {
            outputButton.className = `${canUseInRecipe ? (isOutput ? 'bg-violet-700 text-white' : 'bg-gray-800 text-gray-300 hover:bg-gray-700') : 'bg-gray-950 text-gray-700 cursor-not-allowed'} text-[11px] px-2 py-1 rounded`;
        }

        const materialButton = card.querySelector('[data-material-button]');
        if (materialButton) {
            materialButton.textContent = canUseInRecipe ? (isInput ? '재료 해제' : '재료') : '선택 불가';
            materialButton.className = `${canUseInRecipe ? (isInput ? 'bg-blue-700 text-white' : 'bg-gray-800 text-gray-300 hover:bg-gray-700') : 'bg-gray-950 text-gray-700 cursor-not-allowed'} text-[11px] px-2 py-1 rounded`;
        }
    }
}

function filterStoryletItemList(input) {
    const query = input.value.trim().toLowerCase();
    const root = input.closest('[data-storylet-form]');
    if (!root) return;

    for (const card of root.querySelectorAll('[data-storylet-item-card]')) {
        const text = card.dataset.itemSearch || '';
        card.classList.toggle('hidden', query.length > 0 && !text.includes(query));
    }
}

function applyStoryletItemEditorDrafts(root) {
    for (const container of root.querySelectorAll('[data-item-editor]')) {
        const item = storyletItemById(container.dataset.itemEditor);
        if (!item) continue;
        for (const el of container.querySelectorAll('[data-item-field]')) {
            item[el.dataset.itemField] = toCsvEditorText(el.dataset.itemField, el.value);
        }
    }
}

function buildStoryletObjectContextHtml(row) {
    const area = areaTypeLabel(num(row.target_area_type));
    const objectType = num(row.target_object_type);
    const interactables = storyletMatchingInteractables(row);

    const objectHtml = interactables.length > 0
        ? interactables.slice(0, 5).map(item => `
            <div data-interactable-editor="${escapeAttr(item.id)}" class="border-t border-gray-800 first:border-t-0 py-3">
                <div class="flex flex-wrap items-center justify-between gap-2 mb-2">
                    <div>
                        <span class="text-gray-200 font-semibold">${escapeHtml(item.short_name_kr || `Interactable ${item.id}`)}</span>
                        <span class="text-gray-600 ml-2">#${escapeHtml(item.id)}</span>
                        <span class="text-gray-600 ml-2">${escapeHtml(areaTypeLabel(num(item.area_type)))} · (${escapeHtml(item.cell_x)}, ${escapeHtml(item.cell_y)})</span>
                    </div>
                </div>
                <div class="grid grid-cols-1 md:grid-cols-3 gap-2 mb-2">
                    <label class="text-[11px] text-gray-500">오브젝트명 KR
                        <input data-interactable-field="short_name_kr" value="${escapeAttr(item.short_name_kr ?? '')}"
                               class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                    </label>
                    <label class="text-[11px] text-gray-500">EN
                        <input data-interactable-field="short_name_en" value="${escapeAttr(item.short_name_en ?? '')}"
                               class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                    </label>
                    <label class="text-[11px] text-gray-500">JP
                        <input data-interactable-field="short_name_jp" value="${escapeAttr(item.short_name_jp ?? '')}"
                               class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100" />
                    </label>
                </div>
                <label class="block text-[11px] text-violet-300">선택지 클릭 전 본문 KR
                    <textarea data-interactable-field="description_kr"
                              class="mt-1 w-full bg-gray-900 border border-violet-900 rounded px-2 py-1 text-gray-100 leading-relaxed"
                              style="min-height: 5rem;">${escapeEditorText(item.description_kr)}</textarea>
                </label>
                <div class="grid grid-cols-1 md:grid-cols-2 gap-2 mt-2">
                    <label class="text-[11px] text-gray-500">본문 EN
                        <textarea data-interactable-field="description_en"
                                  class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100"
                                  style="min-height: 4rem;">${escapeEditorText(item.description_en)}</textarea>
                    </label>
                    <label class="text-[11px] text-gray-500">본문 JP
                        <textarea data-interactable-field="description_jp"
                                  class="mt-1 w-full bg-gray-900 border border-gray-700 rounded px-2 py-1 text-gray-100"
                                  style="min-height: 4rem;">${escapeEditorText(item.description_jp)}</textarea>
                    </label>
                </div>
            </div>
        `).join('')
        : `<div class="text-gray-600">매칭되는 interactable_info 행이 없습니다. area=${escapeHtml(row.target_area_type)}, object=${escapeHtml(row.target_object_type)}</div>`;

    return `
        <div class="bg-gray-950 border border-gray-800 rounded px-3 py-2 text-xs text-gray-400">
            <div class="flex items-center justify-between mb-1">
                <span class="font-semibold text-violet-300">선택지 전 본문: ${escapeHtml(area)} · ${escapeHtml(storyletObjectLabel(row))}</span>
                <span class="text-gray-600">object_type ${escapeHtml(objectType)}</span>
            </div>
            ${objectHtml}
        </div>
    `;
}

function isStoryletAvailable(row, ownedTags) {
    const owned = new Set(ownedTags);
    const all = splitTags(row.required_all_tags);
    const any = splitTags(row.required_any_tags);
    if (!all.every(tag => owned.has(tag))) return false;
    if (any.length > 0 && !any.some(tag => owned.has(tag))) return false;
    return true;
}

function buildStoryletLookupIndexes() {
    _storyletInteractablesByAreaObject = new Map();
    _storyletInteractablesById = new Map();
    _storyletInteractablesByObject = new Map();
    _storyletActionsByObject = new Map();
    _storyletActionsByGroup = new Map();
    _storyletGraphNodesByPart = new Map();
    _storyletRecipesById = new Map();
    _storyletRecipesByOutputPart = new Map();
    _storyletItemsByPart = new Map();
    _storyletItemsById = new Map();
    _areaLabelsFromCsv = new Map();

    for (const area of _storyletData?.areas ?? []) {
        const areaType = num(area.area_type);
        const label = area.name_kr || area.name_en || area.name_jp;
        if (label) _areaLabelsFromCsv.set(areaType, label);
    }

    for (const item of _storyletData?.interactables ?? []) {
        const area = num(item.area_type);
        const object = num(item.object_type);
        const id = num(item.id);
        const areaKey = `${area}:${object}`;
        if (!_storyletInteractablesByAreaObject.has(areaKey)) _storyletInteractablesByAreaObject.set(areaKey, []);
        if (!_storyletInteractablesByObject.has(object)) _storyletInteractablesByObject.set(object, []);
        if (id > 0) _storyletInteractablesById.set(id, item);
        _storyletInteractablesByAreaObject.get(areaKey).push(item);
        _storyletInteractablesByObject.get(object).push(item);
    }

    for (const action of _storyletData?.objectActions ?? []) {
        const object = num(action.object_type);
        const groupKey = action.action_group_key || defaultActionGroupKey(object);
        if (!_storyletActionsByObject.has(object)) _storyletActionsByObject.set(object, []);
        if (groupKey && !_storyletActionsByGroup.has(groupKey)) _storyletActionsByGroup.set(groupKey, []);
        _storyletActionsByObject.get(object).push(action);
        if (groupKey) _storyletActionsByGroup.get(groupKey).push(action);
    }

    for (const node of _storyletData?.graphNodes ?? []) {
        const partId = num(node.output_part_id);
        if (partId > 0) _storyletGraphNodesByPart.set(partId, node);
    }

    for (const recipe of _storyletData?.graphRecipes ?? []) {
        const recipeId = num(recipe.recipe_id);
        const outputPartId = num(recipe.output_part_id);
        if (recipeId > 0) _storyletRecipesById.set(recipeId, recipe);
        if (outputPartId > 0) _storyletRecipesByOutputPart.set(outputPartId, recipe);
    }

    for (const item of _storyletData?.items ?? []) {
        const itemId = num(item.id);
        const partId = partIdFromItemId(item.id);
        if (itemId > 0) _storyletItemsById.set(itemId, item);
        if (partId > 0) _storyletItemsByPart.set(partId, item);
    }

    // object_type → 대표 한글 라벨 (매직넘버 가시화용; interactable_info에서 처음 등장한 이름)
    _storyletObjectTypeLabels = new Map();
    for (const item of _storyletData?.interactables ?? []) {
        const obj = num(item.object_type);
        if (obj > 0 && !_storyletObjectTypeLabels.has(obj) && item.short_name_kr) {
            _storyletObjectTypeLabels.set(obj, item.short_name_kr);
        }
    }
}

function storyletMatchingInteractables(row) {
    const interactId = num(row.interact_id);
    if (interactId > 0) {
        const exactInteractable = _storyletInteractablesById.get(interactId);
        if (exactInteractable) return [exactInteractable];
    }

    const area = num(row.target_area_type);
    const object = num(row.target_object_type);
    const exact = _storyletInteractablesByAreaObject.get(`${area}:${object}`) ?? [];
    if (exact.length > 0) return exact;
    return _storyletInteractablesByObject.get(object) ?? [];
}

function storyletPrimaryInteractable(row) {
    return storyletMatchingInteractables(row)[0] ?? null;
}

function storyletRecipeForRow(row) {
    const recipeId = num(row.recipe_id);
    if (recipeId > 0) {
        const recipe = _storyletRecipesById.get(recipeId);
        if (recipe) return recipe;
    }

    const outputPartId = num(row.required_output_item_id);
    if (outputPartId > 0) return _storyletRecipesByOutputPart.get(outputPartId) ?? null;
    return null;
}

function storyletPartById(partId) {
    const id = num(partId);
    return id > 0 ? (_storyletGraphNodesByPart.get(id) ?? null) : null;
}

function storyletItemByPartId(partId) {
    const id = num(partId);
    return id > 0 ? (_storyletItemsByPart.get(id) ?? null) : null;
}

function storyletItemById(itemId) {
    const id = num(itemId);
    return id > 0 ? (_storyletItemsById.get(id) ?? null) : null;
}

function partLabel(partId) {
    const id = num(partId);
    const item = storyletItemByPartId(id);
    if (item) return item.name_kr || item.name_en || item.name_jp || `Part ${id}`;
    const part = storyletPartById(id);
    if (!part) return id > 0 ? `Part ${id}` : '아이템 없음';
    return part.title_kr || part.title_en || part.title_jp || `Part ${id}`;
}

function itemLabel(item) {
    if (!item) return '아이템 없음';
    return item.name_kr || item.name_en || item.name_jp || `Item ${item.id}`;
}

function storyletItemCandidates() {
    return [...(_storyletData?.items ?? [])]
        .filter(item => num(item.id) > 0)
        .sort((a, b) => {
            const aStorylet = isStoryletItem(a) ? 0 : 1;
            const bStorylet = isStoryletItem(b) ? 0 : 1;
            return aStorylet - bStorylet || num(a.id) - num(b.id);
        });
}

function isStoryletItem(item) {
    const id = num(item?.id);
    return id >= 700000000 && id < 901000000;
}

function buildStoryletItemIconHtml(item, sizeClass) {
    if (!item?.id) {
        return `<div class="${sizeClass} shrink-0 rounded bg-gray-950 border border-gray-800"></div>`;
    }

    return `
        <div class="${sizeClass} shrink-0 rounded bg-gray-950 border border-gray-800 flex items-center justify-center overflow-hidden">
            <img src="/item-sprites/${escapeAttr(item.id)}.png"
                 alt=""
                 class="max-w-full max-h-full object-contain"
                 onerror="this.style.display='none'; this.parentElement.textContent='${escapeAttr(item.id)}';" />
        </div>
    `;
}

function storyletPreviewText(row) {
    const primary = storyletPrimaryInteractable(row);
    return flattenText(primary?.description_kr || '');
}

function storyletSearchText(row) {
    const interactables = storyletMatchingInteractables(row);
    return interactables.map(item => [
        item.short_name_kr, item.short_name_en, item.short_name_jp,
        item.description_kr, item.description_en, item.description_jp,
    ].join(' ')).join(' ');
}

function storyletObjectLabel(row) {
    const object = num(row.target_object_type);
    if (object === 0) return '오브젝트 없음';

    const matches = storyletMatchingInteractables(row);
    if (matches.length === 0) return `Object ${object}`;

    const names = [...new Set(matches.map(item => item.short_name_kr).filter(Boolean))];
    if (names.length === 0) return `Object ${object}`;
    if (names.length === 1) return names[0];
    return `${names[0]} 외 ${names.length - 1}`;
}

function splitTags(value) {
    return (value || '').split('|').map(tag => tag.trim()).filter(Boolean);
}

function parseIdList(value) {
    return String(value ?? '')
        .match(/\d+/g)
        ?.map(Number)
        .filter(id => id > 0) ?? [];
}

function formatPartIdList(ids) {
    const uniqueIds = [...new Set(ids.map(num).filter(id => id > 0))];
    return `[${uniqueIds.join(',')}]`;
}

function partIdFromItemId(itemId) {
    const id = num(itemId);
    if (id >= 700000000 && id < 901000000) return id % 1000000;
    return 0;
}

function flattenText(value) {
    return fromCsvEditorText(value).replace(/\s+/g, ' ').trim();
}

function fromCsvEditorText(value) {
    return String(value ?? '').replace(/\\n/g, '\n');
}

function toCsvEditorText(fieldName, value) {
    const text = String(value ?? '');
    return isNarrativeTextField(fieldName)
        ? text.replace(/\r\n/g, '\n').replace(/\r/g, '\n').replace(/\n/g, '\\n')
        : text;
}

function escapeEditorText(value) {
    return escapeHtml(fromCsvEditorText(value));
}

function isNarrativeTextField(fieldName) {
    return /^(success_text|description|result_text)_(kr|en|jp)$/.test(fieldName ?? '');
}

function defaultActionGroupKey(objectType) {
    const object = num(objectType);
    return object > 0 ? `object_${object}` : '';
}

function num(value) {
    const n = Number.parseInt(value, 10);
    return Number.isNaN(n) ? 0 : n;
}

function escapeHtml(value) {
    return String(value ?? '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

function escapeAttr(value) {
    return escapeHtml(value).replace(/`/g, '&#96;');
}

// ─── 진입 ──────────────────────────────────────────────────────────────────

buildJobCheckboxes();
loadMatchingConfig();
startPolling();
