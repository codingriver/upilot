"""Finite bulk cleanup safety and Editor HTTP boundary; no real Unity state touched."""
import asyncio
import json
from pathlib import Path
from types import SimpleNamespace

import pytest
from starlette.requests import Request
from upilot_mcp.queue_http import cleanup_endpoint
from upilot_mcp.responses import ok
from test_queue_cleanup import Service


def args():
    return dict(target_type='All', target_id='*', action='force_clear_all', reason='fixture only')


async def prepare(service):
    result = await service.queue_cleanup(**args())
    assert result.ok, result
    return dict(args(), dry_run=False, confirm_token=result.data['confirmToken'],
                expected_project_path=service.server.state.project_path), result.data


async def finish(service, response):
    assert response.ok, response
    task = service._queue_bulk_handles.get(response.data['requestId'])
    if task:
        await task
    return await service.queue_cleanup(target_type='All', target_id=response.data['requestId'],
        action='force_clear_status', expected_project_path=service.server.state.project_path)


def test_bulk_single_use_and_incomplete_inventory_never_claims_all_cleared(tmp_path):
    async def run():
        service = Service(tmp_path)
        service.task('original')
        apply, preview = await prepare(service)
        assert not service.calls
        result = await service.queue_cleanup(**apply)
        assert result.data['requestId'] == preview['requestId']
        status = await finish(service, result)
        assert service.calls == ['original']
        assert not status.data['allCleared']
        assert status.data['dispatchComplete']
        assert not (await service.queue_cleanup(**apply)).ok
        assert service.calls == ['original']
    asyncio.run(run())


@pytest.mark.parametrize('change', ['membership', 'identity', 'permission', 'project'])
def test_bulk_apply_revalidates_before_any_side_effect(tmp_path, change):
    async def run():
        service = Service(tmp_path)
        state = service.task('original')
        apply, _ = await prepare(service)
        if change == 'membership':
            service.task('new')
        if change == 'identity':
            state['runGuid'] = 'replacement'
            service.server.state.save_test_job(state)
        if change == 'permission':
            (tmp_path / '.upilot').mkdir(exist_ok=True)
            (tmp_path / '.upilot/config.json').write_text('{"aiQueueCleanupAllowed":false}')
        if change == 'project':
            apply['expected_project_path'] += '/other'
        result = await service.queue_cleanup(**apply)
        assert not result.ok
        assert service.calls == []
    asyncio.run(run())


def test_interrupted_journal_is_observed_not_replayed(tmp_path):
    async def run():
        service = Service(tmp_path)
        path = service._bulk_path(str(tmp_path), 'bulk-original')
        path.parent.mkdir(parents=True)
        path.write_text(json.dumps(dict(projectPath=str(tmp_path), status='running', targets=[
            dict(id='original', status='dispatching_unconfirmed')], allCleared=False)))
        result = await service.queue_cleanup(target_type='All', target_id='bulk-original', action='force_clear_status')
        assert result.data['status'] == 'interrupted_unconfirmed'
        assert not result.data['allCleared']
        assert not service.calls
        assert json.loads(path.read_text())['status'] == 'running', 'Status is read-only'
    asyncio.run(run())


def http_request(body, *, host='127.0.0.1', origin='', peer='127.0.0.1', marker='1'):
    headers = [(b'host', (host + ':8011').encode()), (b'content-type', b'application/json'),
               (b'x-upilot-queue', marker.encode())]
    if origin:
        headers.append((b'origin', origin.encode()))
    async def receive():
        return dict(type='http.request', body=json.dumps(body).encode(), more_body=False)
    return Request(dict(type='http', method='POST', scheme='http', path='/queue/cleanup',
        query_string=b'', headers=headers, server=('127.0.0.1', 8011), client=(peer, 2000)), receive)


@pytest.mark.parametrize('override', [dict(origin='http://evil.test'), dict(host='evil.test'),
    dict(peer='192.168.0.2'), dict(marker='')])
def test_editor_endpoint_rejects_browser_remote_and_rebinding(override):
    async def run():
        async def forbidden(**kwargs):
            pytest.fail('Must not call queue mutation')
        result = await cleanup_endpoint(http_request({}, **override), SimpleNamespace(queue_cleanup=forbidden))
        assert result.status_code == 403
    asyncio.run(run())


def test_editor_endpoint_uses_same_finite_contract(tmp_path):
    async def run():
        service = Service(tmp_path)
        body = dict(targetType='All', targetId='*', action='force_clear_all', reason='fixture',
                    expectedProjectPath=str(tmp_path), dryRun=True)
        result = await cleanup_endpoint(http_request(body), service)
        assert result.status_code == 200 and json.loads(result.body)['ok']
        body['action'] = 'arbitrary_method'
        result = await cleanup_endpoint(http_request(body), service)
        assert result.status_code == 400
        assert not service.calls
    asyncio.run(run())


def test_journal_failure_does_not_prevent_stop_or_in_memory_status(tmp_path, monkeypatch):
    from upilot_mcp.domain import queue_bulk
    def cannot_write(*args):
        raise OSError("fixture disk full")
    async def run():
        service = Service(tmp_path)
        service.task('original')
        apply, _ = await prepare(service)
        monkeypatch.setattr(queue_bulk, '_write_journal', cannot_write)
        result = await service.queue_cleanup(**apply)
        status = await finish(service, result)
        assert service.calls == ['original']
        assert status.ok and status.data['terminal']
        assert status.data['dispatchComplete']
        assert 'fixture disk full' in status.data['persistenceError']
        assert not (await service.queue_cleanup(**apply)).ok
        assert service.calls == ['original']
    asyncio.run(run())
