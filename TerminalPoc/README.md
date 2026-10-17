# SSH Terminal PoC

Small WPF application for evaluating `VirtualTerminal.WPF` with its SSH.NET-backed `SecureShellSession`.

The PoC uses a local source copy of [VirtualTerminal](https://github.com/Rikitav/VirtualTerminal), licensed under Apache-2.0 (see `VirtualTerminal-upstream/LICENSE.txt`). Its WPF renderer is patched to paint all cell backgrounds before glyphs and to advance wide glyphs by two terminal cells. This fixes CJK text being clipped by nano's inverse-video status and prompt rows.

## Run

Requires the .NET 10 SDK and network access to NuGet for the first restore.

```powershell
dotnet restore
dotnet run
```

Enter a host, port, username, and password, then select **Connect**. On connection, compare the shown SHA256 host-key fingerprint through a trusted channel and accept only if it matches. The PoC asks on every connection and does not persist a known-hosts database. The password is used only for this process and is not saved. This first PoC intentionally tests password authentication only; key authentication and SFTP are outside its scope.

## Terminal evaluation checklist

- Connect to the intended SSH server and run ordinary shell commands.
- Check Chinese input/output, selection, copy/paste, and scrollback.
- In nano, verify Ctrl+X, Ctrl+O, and Ctrl+W reach the remote shell.
- Resize the window and verify the remote terminal's rows and columns follow it.
- Try the interactive programs used day to day (for example `vim`, `tmux`, `htop`, or Claude CLI).
- Disconnect during output and close the window; confirm the UI stays responsive and the session ends.

Known PoC limitations: no persistent known-hosts database, no key-based auth, no reconnection, no SFTP, no persistence, and no production-grade error presentation. Do not use it to store credentials.
