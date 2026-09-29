import asyncio
import hashlib
from pathlib import Path

import pytest
from upilot_mcp.responses import ok
from test_queue_disposition import Service, prepare_task
from test_persistent_operations import stop_observers


class AbandonService(Service):
    def __init__(self, root):
        super().__init__(root)
        self.sent = []
        self.hidden = False
        self.lose = False

    async def bridge_call(self, request, route, payload, **kwargs):
        if route == 'test.abandon_preview':
            return ok(request, dict(eligible=True, runGuid='test-original', stateHash='a' * 64,
                adapter='test-post-reload-v1', originalCallbackDomain='old', currentCallbackDomain='new'))
        if route == 'test.abandon':
            self.sent.append(payload)
            path = Path(self.server.state.project_path) / 'original-unity.json'
            path.write_bytes(b'original unresolved resource evidence')
            self.test_sample = dict(runGuid='test-original', status='Released', terminal=True,
                cleanupPending=False, cleanupSucceeded=False, cleanupResourcesReleased=False,
                resultAuthoritative=True, outcomeStatus='failed', failed=1, unresolvedResources=['old callback'],
                disposition=dict(requestId=request, runGuid='test-original', adapter='test-post-reload-v1',
                    originalCallbackDomain='old', releasedInDomain='new', backupPath=str(path),
                    backupBytes=path.stat().st_size, backupSha256=hashlib.sha256(path.read_bytes()).hexdigest()))
            if self.lose:
                raise TimeoutError('response lost')
        return ok(request, {})

    async def test_results(self, run_guid=''):
        return ok('sample', {} if self.hidden else self.test_sample)


async def apply(service, state):
    args = dict(target_type='Task', target_id=state['taskId'], action='abandon', reason='fixture')
    preview = await service.queue_cleanup(**args)
    assert preview.ok, preview
    return await service.queue_cleanup(**args, dry_run=False, confirm_token=preview.data['confirmToken'],
        expected_project_path=service.server.state.project_path)


def test_abandon_preserves_original_result_and_is_not_acceptance_success(tmp_path):
    async def run():
        service = AbandonService(tmp_path)
        state = prepare_task(service)
        result = await apply(service, state)
        assert result.ok and state['terminal'], result
        assert state['status'] == 'Released' and state['outcome'] == 'failed'
        assert not state['result']['result']['acceptancePassed']
        assert state['originalTestResult']['unresolvedResources'] == ['old callback']
        assert len(service.sent) == 1
        await stop_observers(service)
    asyncio.run(run())


def test_abandon_lost_response_finishes_by_observation_without_second_dispatch(tmp_path):
    async def run():
        service = AbandonService(tmp_path)
        state = prepare_task(service)
        service.lose = service.hidden = True
        result = await apply(service, state)
        assert result.ok and not state['terminal'], result
        service.hidden = False
        state['nextRecoveryObservationAt'] = 0
        await asyncio.wait_for(service._observe_test_recovery(state), 2)
        assert state['terminal'] and state['status'] == 'Released'
        assert len(service.sent) == 1
        await stop_observers(service)
    asyncio.run(run())


@pytest.mark.parametrize('fault', ['backup', 'identity', 'pending'])
def test_abandon_receipt_requires_identity_backup_and_final_commit(tmp_path, fault):
    async def run():
        service = AbandonService(tmp_path)
        state = prepare_task(service)
        service.hidden = True
        await apply(service, state)
        sample = service.test_sample
        if fault == 'backup': Path(sample['disposition']['backupPath']).write_bytes(b'changed')
        if fault == 'identity': sample['disposition']['requestId'] = 'other'
        if fault == 'pending': sample['cleanupPending'] = True
        service._observe_test_abandon(state, sample)
        assert not state['terminal'] and not state.get('disposition')
        assert len(service.sent) == 1
        await stop_observers(service)
    asyncio.run(run())


def test_abandon_probe_reports_original_bridge_refusal_without_dispatch(tmp_path):
    from upilot_mcp.responses import fail
    async def run():
        service = AbandonService(tmp_path)
        state = prepare_task(service)
        async def refused(request, route, payload, **kwargs):
            assert route == 'test.abandon_preview'
            return fail(request, 'TEST_ABANDON_RUNNER_NOT_INACTIVE', 'Original Runner remains active.')
        service.dispatcher.call = refused
        preview = await service.queue_cleanup(target_type='Task', target_id=state['taskId'],
            action='abandon', reason='fixture')
        assert not preview.ok
        assert 'TEST_ABANDON_RUNNER_NOT_INACTIVE' in preview.error.message
        assert not service.sent
        await stop_observers(service)
    asyncio.run(run())
