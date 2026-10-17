"""Explicit synchronized-connection fixtures for tests of post-bootstrap behavior."""
import json
import time

from upilot_mcp.server import _BridgeConnection
from upilot_mcp.version import version_payload


def mark_bridge_ready(server, socket=None):
    """Seed the transport seam only; protocol admission has separate end-to-end tests."""
    session = server.session_manager.active
    assert session is not None
    if socket is None:
        socket = server._ws or object()
    server._ws = socket
    connection = _BridgeConnection(socket, session, "fixture-hello", server.state.lifecycle_id,
                                   version_payload()["server_instance_id"], time.monotonic() + 300)
    connection.phase = "ready"
    connection.receipt = {"bootstrapId": "fixture-bootstrap"}
    server._connection = connection
    assert server.session_manager.mark_ready(session)
    return connection


def bootstrap_payload(server, command, *, session=None, epoch=None, project_id=None):
    from test_bridge_readiness import snapshot, diagnostics
    active = server.session_manager.active
    state = snapshot(sequence=server.state.sequence + 1, domain=server.state.domain_generation,
                     epoch=epoch or server.state.producer_epoch or "fixture-epoch",
                     session=session or active.session_id, pid=active.process_id, created=active.process_created_at)
    state["projectId"] = project_id or server.state.project_id or "fixture-project"
    return {**command["payload"], "state": state, "compileEvidence": diagnostics(state)}


async def synchronize_connection(server, socket, auth):
    from test_bridge_readiness import message
    connection = server._connection
    await server._handle_message(message("event", "session.ready", {**server._bootstrap_identity(connection), "bridgeReadyContractVersion": 1},
                                        session=auth[0]), auth, socket)
    command = json.loads(socket.sent[-1])
    await server._handle_message(message("result", "session.bootstrap", bootstrap_payload(server, command),
                                        identity=command["id"], session=auth[0]), auth, socket)
    assert server.is_ready()


async def synchronize_peer(server, peer, ack, *, session, epoch=None):
    from test_bridge_readiness import message
    from upilot_mcp.protocol import to_wire
    identity = {"bridgeReadyContractVersion": 1, "helloId": ack["id"], "serverLifecycleId": ack["payload"]["serverLifecycleId"],
                "serverInstanceId": ack["payload"]["server_instance_id"]}
    await peer.send(json.dumps(to_wire(message("event", "session.ready", identity, session=session))))
    command = json.loads(await peer.recv())
    assert command["name"] == "session.bootstrap"
    await peer.send(json.dumps(to_wire(message("result", "session.bootstrap",
        bootstrap_payload(server, command, session=session, epoch=epoch), identity=command["id"], session=session))))
    bootstrap_ack = json.loads(await peer.recv())
    assert bootstrap_ack["name"] == "session.bootstrap.ack"
