# macOS platform adapter

Implements Core's interactive screenshot, permission and global-hotkey ports for macOS 15.2+. It may reference Core and the private native C ABI, but no other production project may reference its native implementation details. The adapter must not perform OCR, persist jobs, monitor arbitrary keyboard input, or display a permission prompt except through an explicit permission request.
