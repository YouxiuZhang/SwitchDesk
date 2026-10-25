"""Local simulated peer. No real clipboard, input, screen, pairing store or external network."""
import base64
import hashlib
import json
import os
from pathlib import Path
import socket
import ssl
import struct
import subprocess
import tempfile
import threading
import time
import unittest

ROOT = Path(__file__).resolve().parents[2]
DRIVER = ROOT / '.build/product-checks/session-driver'


def receive(stream):
    def exact(n):
        data = b''
        while len(data) < n:
            chunk = stream.recv(n - len(data))
            if not chunk:
                raise EOFError()
            data += chunk
        return data
    length, = struct.unpack('>I', exact(4))
    if not 0 < length <= 12 * 1024 * 1024:
        raise ValueError('invalid frame')
    return json.loads(exact(length).decode('utf8'))


def send(stream, message, fragmented=False):
    payload = json.dumps(message, ensure_ascii=False).encode('utf8')
    frame = struct.pack('>I', len(payload)) + payload
    if fragmented:
        for end, start in ((1, 0), (3, 1), (7, 3), (len(frame), 7)):
            stream.sendall(frame[start:end])
            time.sleep(.01)
    else:
        stream.sendall(frame)


@unittest.skipUnless(DRIVER.exists(), 'compile the Swift session driver first')
class TLSSessionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory(prefix='switchdesk-tls-')
        cls.directory = Path(cls.tmp.name)
        subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1',
                        '-subj', '/CN=SwitchDesk Loopback Test', '-keyout', str(cls.directory/'key.pem'),
                        '-out', str(cls.directory/'cert.pem')], check=True, capture_output=True)
        der = ssl.PEM_cert_to_DER_cert((cls.directory/'cert.pem').read_text())
        cls.pin = hashlib.sha256(der).hexdigest()

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def exercise(self, wrong_pin):
        listener = socket.socket()
        listener.bind(('127.0.0.1', 0))
        listener.listen(4)
        listener.settimeout(10)
        port = listener.getsockname()[1]
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.minimum_version = ssl.TLSVersion.TLSv1_2
        context.load_cert_chain(self.directory/'cert.pem', self.directory/'key.pem')
        token = base64.b64encode(os.urandom(32)).decode()
        invitation = {'host':'127.0.0.1','port':port,'pin':'0'*64 if wrong_pin else self.pin,'token':token}
        invite_path = self.directory / ('wrong.switchdesk' if wrong_pin else 'good.switchdesk')
        invite_path.write_text('switchdesk1:'+base64.b64encode(json.dumps(invitation).encode()).decode())
        evidence = {'control':False,'media':False,'ack':False,'refused':False}
        errors = []

        def peer():
            control = media = None
            try:
                raw, _ = listener.accept()
                raw.settimeout(10)
                try:
                    control = context.wrap_socket(raw, server_side=True)
                except ssl.SSLError:
                    evidence['refused'] = True
                    return
                hello = receive(control)
                assert hello['channel']=='control' and hello['token']==token and hello['version']==1
                evidence['control'] = True
                send(control, {'type':'welcome','version':1,'channel':'control'}, fragmented=True)
                raw_media, _ = listener.accept()
                raw_media.settimeout(10)
                media = context.wrap_socket(raw_media, server_side=True)
                media_hello = receive(media)
                assert media_hello['channel']=='media' and media_hello['device']==hello['device'] and media_hello['token']==token
                evidence['media'] = True
                send(media, {'type':'welcome','version':1,'channel':'media'})
                send(control, {'type':'state','epoch':3,'source':'windows','target':'windows','ready':True,'armed':False,'switching':False})
                send(media, {'type':'clipboard','id':'test','origin':'windows','kind':'text','text':'双栖 TLS\n🙂','revision':1}, fragmented=True)
                ack=receive(media)
                assert ack=={'type':'test_ack','received':'clipboard'}
                evidence['ack']=True
                send(control, {'type':'ping'})
                end = time.monotonic()+3
                while time.monotonic()<end:
                    message=receive(control)
                    if message['type']=='ping': send(control, {'type':'pong'})
                    if message['type']=='pong':
                        time.sleep(.15)
                        send(control, {'type':'test_finish'})
                        return
                raise AssertionError('no pong')
            except Exception as exc:
                errors.append(repr(exc))
            finally:
                for conn in (control,media):
                    if conn:
                        conn.close()

        thread=threading.Thread(target=peer,daemon=True)
        thread.start()
        result=subprocess.run([str(DRIVER),str(invite_path),'reject' if wrong_pin else 'accept'],
                              capture_output=True,text=True,timeout=20)
        listener.close()
        thread.join(2)
        self.assertEqual(result.returncode,0,result.stdout+result.stderr)
        self.assertFalse(errors,errors)
        if wrong_pin:
            self.assertTrue(evidence['refused'],evidence)
            self.assertFalse(evidence['control'],evidence)
        else:
            self.assertTrue(all(evidence[k] for k in ('control','media','ack')),evidence)

    def test_pinned_dual_channel_interoperability(self):
        self.exercise(False)

    def test_wrong_certificate_pin_is_rejected(self):
        self.exercise(True)


if __name__ == '__main__':
    unittest.main()
