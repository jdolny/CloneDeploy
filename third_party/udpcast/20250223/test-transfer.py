from pathlib import Path
import hashlib, os, subprocess, time, sys

root = Path(__file__).resolve().parent
multicast = '--multicast' in sys.argv
folder = root / ('multicast-test' if multicast else 'transfer-test')
folder.mkdir(exist_ok=True)
source = folder / 'input.bin'
source.write_bytes(os.urandom(16 * 1024 * 1024))
binary = root / 'build-x86_64'
common = ['--interface', '127.0.0.1', '--mcast-rdv-address', '127.0.0.1', '--portbase', '39080', '--nokbd', '--start-timeout', '15']
mode = ['--nopointopoint', '--mcast-data-address', '239.255.52.11', '--ttl', '0', '--fec', '8x4/64'] if multicast else ['--pointopoint']
processes = []
try:
    with (folder / 'receiver.log').open('wb') as receiver_log, (folder / 'sender.log').open('wb') as sender_log:
        sender = subprocess.Popen([str(binary / 'udp-sender.exe'), *common, *mode, '--min-receivers', '1', '--max-bitrate', '100m', '--file', str(source)], stdout=sender_log, stderr=sender_log, creationflags=subprocess.CREATE_NO_WINDOW)
        processes.append(sender)
        time.sleep(0.5)
        receiver = subprocess.Popen([str(binary / 'udp-receiver.exe'), *common, '--file', str(folder / 'output.bin'), '--receive-timeout', '15'], stdout=receiver_log, stderr=receiver_log, creationflags=subprocess.CREATE_NO_WINDOW)
        processes.append(receiver)
        for process in processes:
            assert process.wait(timeout=30) == 0, 'Transfer process failed; see logs'
    expected = hashlib.sha256(source.read_bytes()).hexdigest()
    assert hashlib.sha256((folder / 'output.bin').read_bytes()).hexdigest() == expected
    print('PASS: 16 MiB localhost x64', 'multicast with FEC' if multicast else 'unicast', 'transfer, identical SHA-256:', expected)
finally:
    for process in processes:
        if process.poll() is None:
            process.kill()
            process.wait()
