"""Explicit finite bulk cleanup, not a scheduler or an unconditional queue reset."""
from __future__ import annotations

import asyncio
import hashlib
import json
import logging
import os
from pathlib import Path
import secrets
import time

from ..protocol import new_id, now_ms
from ..queue_audit import QUEUE_GUARD, safe_text
from ..responses import fail, ok
from ..service_maintenance import same_path


def _write_journal(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix('.' + secrets.token_hex(8) + '.tmp')
    try:
        with temporary.open('x', encoding='utf-8') as stream:
            json.dump(value, stream, ensure_ascii=False, sort_keys=True)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


class QueueBulkMixin:
    async def _queue_test_abandon_probe(self, value):
        if not self._queue_job_execution_idle() or not self._queue_task_release_eligible(value):
            raise ValueError('QUEUE_ABANDON_UNSUPPORTED: original test/observer/dispatch is not fenced.')
        result = await self.dispatcher.call(new_id('req'), 'test.abandon_preview', {'runGuid': value['runGuid']})
        if not result.ok:
            error = result.error
            code = safe_text(getattr(error, 'code', '') or 'UNKNOWN_BRIDGE_ERROR')
            message = safe_text(getattr(error, 'message', '') or 'Original proof unavailable.')
            raise ValueError(f'QUEUE_ABANDON_UNSUPPORTED: {code}: {message}')
        proof = result.data if isinstance(result.data, dict) else {}
        if (proof.get('eligible') is not True or proof.get('runGuid') != value['runGuid']
                or proof.get('adapter') != 'test-post-reload-v1' or len(str(proof.get('stateHash', ''))) != 64
                or not proof.get('originalCallbackDomain') or not proof.get('currentCallbackDomain')
                or proof['originalCallbackDomain'] == proof['currentCallbackDomain']):
            raise ValueError('QUEUE_ABANDON_UNSUPPORTED: no verified post-Reload orphan proof.')
        return proof

    async def task_abandon(self, target, expected, request, reason):
        state = self._async_tasks.get(target)
        guard = QUEUE_GUARD.get()
        if (not guard or guard[0] != target or guard[1] != expected or state is None
                or not self._queue_task_release_eligible(state) or not self._queue_job_execution_idle()
                or '_abandonEvidence' not in expected):
            return fail(request, 'QUEUE_ABANDON_GRANT_REQUIRED', 'Use exact queue preview/apply.')
        proof = expected['_abandonEvidence']
        intent = self.server.state.backup_job_disposition('Task', state, proof, request, safe_text(reason))
        intent['adapter'] = proof['adapter']
        # Persist before dispatch. Unknown delivery is observation-only, including after Server restart.
        state.update(releaseRequest=intent, phase='abandon_pending', recoveryObservationOnly=True)
        self.server.state.save_test_job(state)
        try:
            await self.dispatcher.call(request, 'test.abandon', dict(runGuid=state['runGuid'],
                requestId=request, expectedStateHash=proof['stateHash'], reason=safe_text(reason)), timeout_ms=30000)
        except Exception as exc:
            state['lastObservationError'] = str(exc)
        response = await self.test_results(run_guid=state['runGuid'])
        self._observe_test_abandon(state, response.data if response.ok else {})
        return ok(request, self._public_task_state(state))

    def _observe_test_abandon(self, state, sample):
        intent = state['releaseRequest']
        try:
            d = sample.get('disposition') or {}
            if (not self._operation_project_matches(state) or sample.get('runGuid') != state['runGuid']
                    or sample.get('status') != 'Released' or sample.get('terminal') is not True
                    or sample.get('cleanupPending') is not False or d.get('requestId') != intent['requestId']
                    or d.get('runGuid') != state['runGuid'] or d.get('adapter') != intent['adapter']
                    or not d.get('originalCallbackDomain') or not d.get('releasedInDomain')
                    or d['originalCallbackDomain'] == d['releasedInDomain']):
                raise ValueError('Original disposition/commit unconfirmed; observe, do not resend.')
            for evidence in (intent, d):
                path = Path(evidence['backupPath']).resolve()
                project = Path(state['projectPath']).resolve()
                if not path.is_relative_to(project):
                    raise ValueError('Backup is outside the original project.')
                data = path.read_bytes()
                if len(data) != evidence['backupBytes'] or hashlib.sha256(data).hexdigest() != evidence['backupSha256']:
                    raise ValueError('QUEUE_BACKUP_VERIFICATION_FAILED')
            candidate = self._released_candidate(state, dict(intent, unity=d, disposedAt=now_ms()))
            candidate['originalTestResult'] = sample
            # Preserve known business result without treating administrative closure as acceptance.
            candidate['businessTerminal'] = sample.get('resultAuthoritative') is True
            candidate['outcome'] = sample.get('outcomeStatus') if candidate['businessTerminal'] else 'unknown'
            if candidate.get('acceptanceReport'):
                candidate['originalAcceptanceReport'] = candidate.pop('acceptanceReport')
            if candidate.get('result'):
                candidate['originalTaskResult'] = candidate['result']
            candidate['result'] = {'result': {'status': 'Released', 'acceptancePassed': False,
                'outcome': candidate['outcome'], 'cleanupSucceeded': False}}
            self.server.state.save_test_job(candidate)
            state.clear()
            state.update(candidate)
        except Exception as exc:
            self._recovery_observation_failed(state, str(exc))
            self.server.state.save_test_job(state)

    def _bulk_path(self, project, identity):
        if not isinstance(identity, str) or not identity.startswith('bulk-') or any(
                c not in 'abcdefghijklmnopqrstuvwxyz0123456789-' for c in identity):
            raise ValueError('Invalid bulk request identity.')
        return Path(project) / 'Library' / 'UPilot' / 'QueueCleanup' / (identity + '.json')

    @staticmethod
    def _bulk_membership(inventory):
        return sorted((row['type'], row['id']) for row in inventory.get('items', []))

    async def queue_bulk(self, kind, target, action, reason, dry_run, token, expected_project):
        from .queue_service import authorization
        request = new_id('bulk')
        try:
            project = self._queue_project(allow_disconnected=action == 'force_clear_status')
            if expected_project and not same_path(project, expected_project):
                return fail(request, 'QUEUE_PROJECT_MISMATCH', 'Target project changed.')
            if kind != 'All':
                return fail(request, 'QUEUE_INVALID_ARGUMENTS', 'Bulk actions require targetType=All.')
            if action == 'force_clear_status':
                value = json.loads(self._bulk_path(project, target).read_text(encoding='utf-8'))
                if not same_path(project, value.get('projectPath', '')):
                    raise ValueError('Journal project mismatch.')
                if value['status'] == 'running' and target not in self.__dict__.get('_queue_bulk_handles', {}):
                    value.update(status='interrupted_unconfirmed', dispatchComplete=False, allCleared=False)
                inventory = await self.queue_inventory()
                current = inventory.data if inventory.ok else {}
                value.update(allCleared=current.get('complete') is True and current.get('items') == [],
                    remaining=current.get('items', []), issues=current.get('issues', ['QUEUE_INVENTORY_UNCONFIRMED']),
                    observedAt=now_ms())
                return ok(request, value)
            if target != '*' or not reason.strip() or len(reason) > 256:
                return fail(request, 'QUEUE_INVALID_ARGUMENTS', 'Use targetId=* and a short reason.')
            previews = self.__dict__.setdefault('_queue_bulk_previews', {})
            for key in list(previews):
                if previews[key]['expires'] < time.monotonic():
                    del previews[key]
            if dry_run:
                if len(previews) >= 16:
                    return fail(request, 'QUEUE_PREVIEW_LIMIT', 'Too many bulk previews.')
                inventory = await self.queue_inventory()
                if not inventory.ok:
                    return inventory
                rows = []
                for row in inventory.data['items']:
                    candidates = []
                    if row['type'] == 'Task':
                        candidates = ['abandon', 'cleanup'] if row['status'] == 'RecoveryRequired' else ['cancel']
                    elif row['type'] == 'Operation':
                        candidates = ['release', 'recover'] if row.get('action') == 'recover' else ([row['action']] if row.get('action') else [])
                    elif row['type'] in {'Test', 'Capture', 'WriteBatch'} and row.get('action'):
                        candidates = ['cleanup' if row['type'] == 'Test' else row['action']]
                    chosen = dict(type=row['type'], id=row['id'], action='', status='unsupported',
                                  reason=row.get('unsupportedReason') or 'No safe adapter.')
                    for candidate in candidates:
                        preview = await self.queue_cleanup(target_type=row['type'], target_id=row['id'],
                            action=candidate, reason=reason)
                        if preview.ok:
                            chosen.update(action=candidate, status='ready', reason='', token=preview.data['confirmToken'])
                            break
                        chosen['reason'] = preview.error.message
                    rows.append(chosen)
                token = secrets.token_urlsafe(32)
                value = dict(projectPath=project, reason=reason, rows=rows, expires=time.monotonic() + 120,
                             membership=self._bulk_membership(inventory.data), requestId=request)
                previews[token] = value
                return ok(request, dict(dryRun=True, projectPath=project, requestId=request, confirmToken=token, expiresInSeconds=120,
                    allowed=authorization(project)[0], complete=inventory.data['complete'], issues=inventory.data['issues'],
                    targets=[{k: v for k, v in row.items() if k != 'token'} for row in rows],
                    warning='Administrative release is not business success. Unsupported/uncertain targets remain blocked.'))
            allowed, denial = authorization(project)
            if not allowed:
                return fail(request, denial, 'Queue cleanup permission is disabled.')
            if not expected_project:
                return fail(request, 'QUEUE_PROJECT_REQUIRED', 'Apply requires the preview project.')
            value = previews.pop(token, None)
            if not value or value['expires'] < time.monotonic() or value['reason'] != reason or value['projectPath'] != project:
                return fail(request, 'QUEUE_PREVIEW_CHANGED', 'Preview expired/consumed/changed; nothing dispatched.')
            handles = self.__dict__.setdefault('_queue_bulk_handles', {})
            if any(not handle.done() for handle in handles.values()):
                return fail(request, 'QUEUE_BULK_BUSY', 'Observe the original bulk request; do not resubmit.')
            inventory = await self.queue_inventory()
            if not inventory.ok or self._bulk_membership(inventory.data) != value['membership']:
                return fail(request, 'QUEUE_PREVIEW_CHANGED', 'Queue membership changed; nothing dispatched.')
            # Validate every ready target before the first mutation. Individual adapters recheck again at dispatch.
            for row in value['rows']:
                if row['status'] != 'ready':
                    continue
                child = self.__dict__.get('_queue_previews', {}).get(row['token'])
                from .queue_service import fingerprint
                original = await self._queue_target(row['type'], row['id'], row['action'])
                signature = fingerprint([project, row['type'], row['id'], row['action'], reason, original])
                if not child or child[0] < time.monotonic() or child[1] != signature:
                    return fail(request, 'QUEUE_PREVIEW_CHANGED', 'A target changed; nothing dispatched.')
            request = value['requestId']
            journal = dict(requestId=request, projectPath=project, status='running', dispatchComplete=False,
                allCleared=False, startedAt=now_ms(), reason=safe_text(reason),
                targets=[{k: v for k, v in row.items() if k != 'token'} for row in value['rows']])
            path = self._bulk_path(project, request)
            _write_journal(path, journal)
            handles[request] = asyncio.create_task(self._execute_bulk(path, journal, value), name=request)
            return ok(request, dict(requestId=request, projectPath=project, status='running', allCleared=False,
                                   nextAction='Observe action=force_clear_status with this requestId; never replay apply.'))
        except Exception as exc:
            return fail(request, 'QUEUE_BULK_FAILED', safe_text(str(exc)))

    async def _execute_bulk(self, path, journal, preview):
        from .queue_service import authorization
        try:
            for row, output in zip(preview['rows'], journal['targets']):
                if row['status'] != 'ready':
                    continue
                if not same_path(self._queue_project(), journal['projectPath']) or not authorization(journal['projectPath'])[0]:
                    raise ValueError('Project/authorization changed; remaining requests not sent.')
                output['status'] = 'dispatching_unconfirmed'
                _write_journal(path, journal)  # durable before any target side effect
                result = await self.queue_cleanup(target_type=row['type'], target_id=row['id'], action=row['action'],
                    reason=preview['reason'], dry_run=False, confirm_token=row['token'],
                    expected_project_path=journal['projectPath'])
                output.update(status='observed' if result.ok else 'refused_or_unconfirmed',
                    resultStatus=(result.data or {}).get('status', ''),
                    requestId=result.request_id, reason='' if result.ok else result.error.code)
                _write_journal(path, journal)
            inventory = await self.queue_inventory()
            data = inventory.data if inventory.ok else {}
            journal.update(status='finished', dispatchComplete=True, finishedAt=now_ms(),
                allCleared=data.get('complete') is True and not data.get('items'),
                remaining=data.get('items', []), issues=data.get('issues', ['QUEUE_INVENTORY_UNCONFIRMED']) if inventory.ok else ['QUEUE_INVENTORY_UNCONFIRMED'])
        except (Exception, asyncio.CancelledError) as exc:
            journal.update(status='interrupted_unconfirmed', dispatchComplete=False, allCleared=False,
                           error=safe_text(str(exc)), finishedAt=now_ms())
        finally:
            try:
                _write_journal(path, journal)
            except Exception:
                logging.getLogger("upilot.mcp").exception("[UPilot][QueueCleanup] Bulk final journal failed; outcome unconfirmed")
                # Original durable dispatching receipt remains unknown; never resend or claim success.
                pass
            self.__dict__.get('_queue_bulk_handles', {}).pop(journal['requestId'], None)
