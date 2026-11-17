// Navigate the visible Game View Remote tab on the phone to a URL (DevTools Page.navigate).
(async () => {
  const pages = await (await fetch('http://127.0.0.1:9333/json')).json();
  const p = pages.find((x) => x.id === process.argv[2]);
  const ws = new WebSocket(p.webSocketDebuggerUrl);
  ws.onopen = () => ws.send(JSON.stringify({ id: 1, method: 'Page.navigate', params: { url: process.argv[3] } }));
  ws.onmessage = () => setTimeout(() => process.exit(0), 300);
  setTimeout(() => process.exit(1), 5000);
})();
