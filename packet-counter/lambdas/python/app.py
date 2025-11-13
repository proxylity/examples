import base64
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional
from dataclasses import dataclass


@dataclass
class Remote:
    IpAddress: str


@dataclass
class Local:
    Domain: str
    Port: int


@dataclass
class Message:
    Tag: str
    Remote: Remote
    Local: Local
    ReceivedAt: str
    Formatter: str
    Data: str


@dataclass
class InboundPackets:
    Messages: List[Message]


@dataclass
class OutboundPacket:
    GeneratedAt: str
    Tag: str
    Data: str


@dataclass
class Response:
    Replies: List[OutboundPacket]


def handler(event: Dict[str, Any], context: Any) -> Dict[str, Any]:
    # Parse inbound packets
    messages = []
    for msg_data in event.get('Messages', []):
        remote = Remote(IpAddress=msg_data['Remote']['IpAddress'])
        local = Local(
            Domain=msg_data['Local']['Domain'],
            Port=msg_data['Local']['Port']
        )
        message = Message(
            Tag=msg_data['Tag'],
            Remote=remote,
            Local=local,
            ReceivedAt=msg_data['ReceivedAt'],
            Formatter=msg_data['Formatter'],
            Data=msg_data['Data']
        )
        messages.append(message)
    
    # Count packets per source IP
    counts: Dict[str, int] = {}
    for msg in messages:
        ip = msg.Remote.IpAddress
        counts[ip] = counts.get(ip, 0) + 1
    
    # Helper to get & clear count and encode
    def get_and_clear(m: Dict[str, int], key: str) -> Optional[str]:
        value = m.get(key, 0)
        if value == 0:
            return None
        m[key] = 0
        encoded = base64.b64encode(f"{value}\n".encode()).decode()
        return encoded

    # Build replies
    replies: List[OutboundPacket] = []
    for msg in messages:
        data = get_and_clear(counts, msg.Remote.IpAddress)
        if data is None:
            continue
        replies.append(OutboundPacket(
            GeneratedAt=datetime.now(timezone.utc).isoformat().replace('+00:00', 'Z'),
            Tag=msg.Tag,
            Data=data
        ))
    
    # Return response
    return {
        'Replies': [
            {
                'GeneratedAt': reply.GeneratedAt,
                'Tag': reply.Tag,
                'Data': reply.Data
            }
            for reply in replies
        ]
    }
