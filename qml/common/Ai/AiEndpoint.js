.pragma library

function hostIsLiteralLoopback(host) {
  return host === "[::1]" || /^127\.\d{1,3}\.\d{1,3}\.\d{1,3}$/.test(host)
}
