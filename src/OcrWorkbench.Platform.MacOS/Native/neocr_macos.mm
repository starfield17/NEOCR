#import <AppKit/AppKit.h>
#import <Carbon/Carbon.h>
#import <CoreGraphics/CoreGraphics.h>
#import <ImageIO/ImageIO.h>
#import <ScreenCaptureKit/ScreenCaptureKit.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>

#include <cstdlib>
#include <cstring>

extern "C" {
typedef void (*neo_hotkey_callback)(void *context);
typedef void (*neo_capture_callback)(int32_t status,
                                     const uint8_t *bytes,
                                     size_t length,
                                     int32_t pixel_width,
                                     int32_t pixel_height,
                                     const char *error_message,
                                     void *context);

int32_t neocr_macos_abi_version(void);
int32_t neocr_hotkey_register(uint32_t virtual_key_code,
                              uint32_t modifiers,
                              neo_hotkey_callback callback,
                              void *context);
void neocr_hotkey_unregister(void);
int32_t neocr_capture_begin(neo_capture_callback callback, void *context);
void neocr_capture_cancel(void);
void neocr_buffer_free(void *buffer);
}

static EventHotKeyRef g_hotkey = nullptr;
static EventHandlerRef g_hotkey_handler = nullptr;
static neo_hotkey_callback g_hotkey_callback = nullptr;
static void *g_hotkey_context = nullptr;

static OSStatus neo_hotkey_event_handler(EventHandlerCallRef, EventRef, void *) {
    if (g_hotkey_callback != nullptr) {
        g_hotkey_callback(g_hotkey_context);
    }
    return noErr;
}

@class NeoCaptureSession;

@interface NeoOverlayWindow : NSWindow
@end

@implementation NeoOverlayWindow
- (BOOL)canBecomeKeyWindow { return YES; }
@end

@interface NeoSelectionView : NSView
@property(nonatomic, weak) NeoCaptureSession *session;
@end

@interface NeoCaptureSession : NSObject
@property(nonatomic, assign) neo_capture_callback callback;
@property(nonatomic, assign) void *context;
@property(nonatomic, strong) NSMutableArray<NSWindow *> *windows;
@property(nonatomic, assign) NSPoint startPoint;
@property(nonatomic, assign) NSPoint currentPoint;
@property(nonatomic, assign) BOOL dragging;
@property(nonatomic, assign) BOOL completed;
@property(nonatomic, assign) BOOL cursorPushed;
- (instancetype)initWithCallback:(neo_capture_callback)callback context:(void *)context;
- (void)begin;
- (void)updateSelectionFromEvent:(NSEvent *)event inView:(NeoSelectionView *)view;
- (void)finishSelectionFromEvent:(NSEvent *)event inView:(NeoSelectionView *)view;
- (void)cancel;
- (NSRect)selectionRect;
@end

static NeoCaptureSession *g_capture_session = nil;

@implementation NeoSelectionView
- (BOOL)acceptsFirstResponder { return YES; }
- (BOOL)isFlipped { return NO; }

- (void)drawRect:(NSRect)dirtyRect {
    [super drawRect:dirtyRect];
    [[NSColor colorWithWhite:0 alpha:0.30] setFill];
    NSRectFill(self.bounds);

    NSRect globalSelection = self.session.selectionRect;
    if (!NSIsEmptyRect(globalSelection)) {
        NSRect localSelection = [self.window convertRectFromScreen:globalSelection];
        CGContextClearRect([NSGraphicsContext currentContext].CGContext, NSRectToCGRect(localSelection));
        [[NSColor whiteColor] setStroke];
        NSBezierPath *border = [NSBezierPath bezierPathWithRect:localSelection];
        border.lineWidth = 2.0;
        [border stroke];
    }
}

- (void)mouseDown:(NSEvent *)event {
    [self.window makeKeyWindow];
    [self.window makeFirstResponder:self];
    NSPoint point = [self.window convertPointToScreen:event.locationInWindow];
    self.session.startPoint = point;
    self.session.currentPoint = point;
    self.session.dragging = YES;
    [self.session updateSelectionFromEvent:event inView:self];
}

- (void)mouseDragged:(NSEvent *)event {
    [self.session updateSelectionFromEvent:event inView:self];
}

- (void)mouseUp:(NSEvent *)event {
    [self.session finishSelectionFromEvent:event inView:self];
}

- (void)keyDown:(NSEvent *)event {
    if (event.keyCode == 53) {
        [self.session cancel];
    } else {
        [super keyDown:event];
    }
}
@end

@implementation NeoCaptureSession
- (instancetype)initWithCallback:(neo_capture_callback)callback context:(void *)context {
    self = [super init];
    if (self) {
        _callback = callback;
        _context = context;
        _windows = [NSMutableArray array];
    }
    return self;
}

- (void)begin {
    if (!CGPreflightScreenCaptureAccess() && !CGRequestScreenCaptureAccess()) {
        [self completeWithStatus:2 bytes:nullptr length:0 width:0 height:0 error:"Screen Recording permission is required. Grant access in System Settings, then relaunch NEOCR."];
        return;
    }

    [NSApp activateIgnoringOtherApps:YES];
    for (NSScreen *screen in NSScreen.screens) {
        NSWindow *window = [[NeoOverlayWindow alloc] initWithContentRect:screen.frame
                                                               styleMask:NSWindowStyleMaskBorderless
                                                                 backing:NSBackingStoreBuffered
                                                                   defer:NO];
        window.level = NSScreenSaverWindowLevel;
        window.opaque = NO;
        window.backgroundColor = NSColor.clearColor;
        window.hasShadow = NO;
        window.collectionBehavior = NSWindowCollectionBehaviorCanJoinAllSpaces |
                                    NSWindowCollectionBehaviorFullScreenAuxiliary;
        NeoSelectionView *view = [[NeoSelectionView alloc] initWithFrame:NSMakeRect(0, 0, screen.frame.size.width, screen.frame.size.height)];
        view.session = self;
        window.contentView = view;
        [window orderFrontRegardless];
        [self.windows addObject:window];
    }

    NSWindow *first = self.windows.firstObject;
    [first makeKeyWindow];
    [first makeFirstResponder:first.contentView];
    [NSCursor.crosshairCursor push];
    self.cursorPushed = YES;
}

- (NSRect)selectionRect {
    if (!self.dragging) {
        return NSZeroRect;
    }
    return NSMakeRect(MIN(self.startPoint.x, self.currentPoint.x),
                      MIN(self.startPoint.y, self.currentPoint.y),
                      fabs(self.currentPoint.x - self.startPoint.x),
                      fabs(self.currentPoint.y - self.startPoint.y));
}

- (void)updateSelectionFromEvent:(NSEvent *)event inView:(NeoSelectionView *)view {
    if (!self.dragging || self.completed) return;
    self.currentPoint = [view.window convertPointToScreen:event.locationInWindow];
    for (NSWindow *window in self.windows) {
        [window.contentView setNeedsDisplay:YES];
    }
}

- (void)finishSelectionFromEvent:(NSEvent *)event inView:(NeoSelectionView *)view {
    if (!self.dragging || self.completed) return;
    self.currentPoint = [view.window convertPointToScreen:event.locationInWindow];
    NSRect selected = self.selectionRect;
    if (selected.size.width < 3.0 || selected.size.height < 3.0) {
        [self cancel];
        return;
    }

    self.dragging = NO;
    [self hideWindows];

    NSScreen *mainScreen = NSScreen.screens.firstObject;
    CGFloat captureY = NSMaxY(mainScreen.frame) - NSMaxY(selected);
    CGRect captureRect = CGRectMake(selected.origin.x, captureY, selected.size.width, selected.size.height);
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 50 * NSEC_PER_MSEC), dispatch_get_main_queue(), ^{
        [SCScreenshotManager captureImageInRect:captureRect completionHandler:^(CGImageRef image, NSError *error) {
            if (error != nil || image == nullptr) {
                NSString *message = error.localizedDescription ?: @"ScreenCaptureKit returned no image.";
                [self completeWithStatus:4 bytes:nullptr length:0 width:0 height:0 error:message.UTF8String];
                return;
            }

            NSMutableData *data = [NSMutableData data];
            CGImageDestinationRef destination = CGImageDestinationCreateWithData((__bridge CFMutableDataRef)data,
                                                                                 (__bridge CFStringRef)UTTypePNG.identifier,
                                                                                 1,
                                                                                 nullptr);
            if (destination == nullptr) {
                [self completeWithStatus:4 bytes:nullptr length:0 width:0 height:0 error:"Could not create a PNG encoder."];
                return;
            }
            CGImageDestinationAddImage(destination, image, nullptr);
            BOOL encoded = CGImageDestinationFinalize(destination);
            CFRelease(destination);
            if (!encoded) {
                [self completeWithStatus:4 bytes:nullptr length:0 width:0 height:0 error:"Could not encode the screenshot as PNG."];
                return;
            }

            void *copy = std::malloc(data.length);
            if (copy == nullptr) {
                [self completeWithStatus:4 bytes:nullptr length:0 width:0 height:0 error:"Could not allocate the screenshot buffer."];
                return;
            }
            std::memcpy(copy, data.bytes, data.length);
            [self completeWithStatus:0
                               bytes:static_cast<const uint8_t *>(copy)
                              length:data.length
                               width:static_cast<int32_t>(CGImageGetWidth(image))
                              height:static_cast<int32_t>(CGImageGetHeight(image))
                               error:nullptr];
        }];
    });
}

- (void)cancel {
    if (self.completed) return;
    [self hideWindows];
    [self completeWithStatus:1 bytes:nullptr length:0 width:0 height:0 error:nullptr];
}

- (void)hideWindows {
    for (NSWindow *window in self.windows) {
        [window orderOut:nil];
    }
    [self.windows removeAllObjects];
    if (self.cursorPushed) {
        [NSCursor pop];
        self.cursorPushed = NO;
    }
}

- (void)completeWithStatus:(int32_t)status
                     bytes:(const uint8_t *)bytes
                    length:(size_t)length
                     width:(int32_t)width
                    height:(int32_t)height
                     error:(const char *)error {
    if (self.completed) {
        if (bytes != nullptr) std::free(const_cast<uint8_t *>(bytes));
        return;
    }
    self.completed = YES;
    neo_capture_callback callback = self.callback;
    void *context = self.context;
    g_capture_session = nil;
    if (callback != nullptr) {
        callback(status, bytes, length, width, height, error, context);
    }
}
@end

extern "C" int32_t neocr_macos_abi_version(void) {
    return 1;
}

extern "C" int32_t neocr_hotkey_register(uint32_t virtual_key_code,
                                           uint32_t modifiers,
                                           neo_hotkey_callback callback,
                                           void *context) {
    if (g_hotkey != nullptr || callback == nullptr) return -1;
    EventTypeSpec event_type = {kEventClassKeyboard, kEventHotKeyPressed};
    OSStatus status = InstallApplicationEventHandler(&neo_hotkey_event_handler,
                                                      1,
                                                      &event_type,
                                                      nullptr,
                                                      &g_hotkey_handler);
    if (status != noErr) return status;

    EventHotKeyID identifier = {'NEOC', 1};
    status = RegisterEventHotKey(virtual_key_code,
                                 modifiers,
                                 identifier,
                                 GetApplicationEventTarget(),
                                 0,
                                 &g_hotkey);
    if (status != noErr) {
        RemoveEventHandler(g_hotkey_handler);
        g_hotkey_handler = nullptr;
        return status;
    }
    g_hotkey_callback = callback;
    g_hotkey_context = context;
    return noErr;
}

extern "C" void neocr_hotkey_unregister(void) {
    if (g_hotkey != nullptr) {
        UnregisterEventHotKey(g_hotkey);
        g_hotkey = nullptr;
    }
    if (g_hotkey_handler != nullptr) {
        RemoveEventHandler(g_hotkey_handler);
        g_hotkey_handler = nullptr;
    }
    g_hotkey_callback = nullptr;
    g_hotkey_context = nullptr;
}

extern "C" int32_t neocr_capture_begin(neo_capture_callback callback, void *context) {
    if (callback == nullptr) return -1;
    if (g_capture_session != nil) return 3;
    dispatch_async(dispatch_get_main_queue(), ^{
        if (g_capture_session != nil) {
            callback(3, nullptr, 0, 0, 0, "A screenshot selection is already active.", context);
            return;
        }
        g_capture_session = [[NeoCaptureSession alloc] initWithCallback:callback context:context];
        [g_capture_session begin];
    });
    return 0;
}

extern "C" void neocr_capture_cancel(void) {
    dispatch_async(dispatch_get_main_queue(), ^{
        [g_capture_session cancel];
    });
}

extern "C" void neocr_buffer_free(void *buffer) {
    std::free(buffer);
}
