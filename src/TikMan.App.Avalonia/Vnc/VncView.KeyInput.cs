using System.Collections.Generic;
using Avalonia.Input;
using Avalonia.Interactivity;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing;

namespace MarcusW.VncClient.Avalonia
{
    public partial class VncView
    {
        private readonly HashSet<KeySymbol> _pressedKeys = new HashSet<KeySymbol>();

        /// <inheritdoc />
        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            if (e.Handled)
                return;

            // Get connection
            RfbConnection? connection = Connection;
            if (connection == null)
                return;

            // Send chars one by one
            foreach (char c in e.Text ?? string.Empty)
            {
                KeySymbol keySymbol = KeyMapping.GetSymbolFromChar(c);

                // Press and release key
                if (!connection.EnqueueMessage(new KeyEventMessage(true, keySymbol)))
                    break;
                connection.EnqueueMessage(new KeyEventMessage(false, keySymbol));
            }

            e.Handled = true;
        }

        /// <inheritdoc />
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || e.Key == Key.None)
                return;

            // Send key press
            if (!HandleKeyEvent(true, e.Key, e.KeyModifiers))
                return;

            e.Handled = true;
        }

        /// <inheritdoc />
        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.Handled || e.Key == Key.None)
                return;

            // Send key release
            if (!HandleKeyEvent(false, e.Key, e.KeyModifiers))
                return;

            e.Handled = true;
        }

        // ⚠️ Avalonia 12 removed the protected virtual OnLostFocus override that the 11.3 adapter used, so
        // this is wired to the LostFocus routed event instead (in the VncView constructor). Clears any keys
        // still held down when focus leaves, so a key isn't left "pressed" on the remote after Alt-Tab.
        private void OnViewLostFocus(object? sender, RoutedEventArgs e) => ResetKeyPresses();

        private bool HandleKeyEvent(bool downFlag, Key key, KeyModifiers keyModifiers)
        {
            // Get connection
            RfbConnection? connection = Connection;
            if (connection == null)
                return false;

            // Might this key be part of a shortcut? When modifies are present, OnTextInput doesn't get called,
            // so we have to handle printable characters here now, too.
            bool includePrintable = (keyModifiers & KeyModifiers.Control) != 0;

            // Get key symbol
            KeySymbol keySymbol = KeyMapping.GetSymbolFromKey(key, includePrintable);
            if (keySymbol == KeySymbol.Null)
                return false;

            // Send key event to server
            bool queued = connection.EnqueueMessage(new KeyEventMessage(downFlag, keySymbol));

            if (downFlag && queued)
                _pressedKeys.Add(keySymbol);
            else if (!downFlag)
                _pressedKeys.Remove(keySymbol);

            return queued;
        }

        private void ResetKeyPresses()
        {
            // (Still) conneced?
            RfbConnection? connection = Connection;
            if (connection != null)
            {
                // Clear pressed keys
                foreach (KeySymbol keySymbol in _pressedKeys)
                {
                    // If the connection is already dead, don't care about clearing them.
                    if (!connection.EnqueueMessage(new KeyEventMessage(false, keySymbol)))
                        break;
                }
            }

            _pressedKeys.Clear();
        }
    }
}
