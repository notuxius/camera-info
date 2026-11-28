
#import <Foundation/Foundation.h>
#import <ImageCaptureCore/ImageCaptureCore.h>
#import <dispatch/dispatch.h>
#import <stdint.h>
#import <stdlib.h>
#import <string.h>

typedef struct {
    uint8_t *bytes;
    size_t length;
    int32_t status;
} CameraResult;

@interface CaptureRequest : NSObject
@property (nonatomic, strong) dispatch_semaphore_t ready;
@property (nonatomic, strong) NSData *data;
@property (nonatomic) int32_t status;
@property (nonatomic) BOOL finished;
- (void)finish:(int32_t)status data:(NSData *)data;
@end

@implementation CaptureRequest
- (instancetype)init {
    self = [super init];
    if (self) self.ready = dispatch_semaphore_create(0);
    return self;
}
- (void)finish:(int32_t)status data:(NSData *)data {
    if (self.finished) return;
    self.finished = YES;
    self.status = status;
    self.data = data;
    dispatch_semaphore_signal(self.ready);
}
@end

@interface CaptureBrowser : NSObject <ICDeviceBrowserDelegate, ICCameraDeviceDelegate>
@property (nonatomic, strong) ICDeviceBrowser *browser;
@property (nonatomic, strong) ICCameraDevice *active;
@property (nonatomic, strong) CaptureRequest *opening;
@property (nonatomic, strong) CaptureRequest *closing;
@property (nonatomic, strong) CaptureRequest *command;
@property (nonatomic) BOOL opened;
@property (nonatomic) BOOL catalogReady;
@end

static NSString *deviceID(ICCameraDevice *camera) {
    return [NSString stringWithFormat:@"%04x:%04x:%08x:%@",
        (unsigned)camera.usbVendorID, (unsigned)camera.usbProductID, (unsigned)camera.usbLocationID,
        camera.UUIDString ?: @""];
}

static NSDictionary *deviceInfo(ICCameraDevice *camera) {
    return @{
        @"id": deviceID(camera),
        @"name": camera.name ?: @"",
        @"vendor": @((uint16_t)camera.usbVendorID),
        @"product": @((uint16_t)camera.usbProductID),
        @"serial": camera.serialNumberString ?: @"",
        @"battery": camera.batteryLevelAvailable && camera.batteryLevel <= 100 ? @(camera.batteryLevel) : NSNull.null,
        @"ptp": @([camera.capabilities containsObject:ICCameraDeviceCanAcceptPTPCommands])
    };
}

static NSData *encodeJSON(id value) {
    return [NSJSONSerialization dataWithJSONObject:value options:0 error:NULL];
}

@implementation CaptureBrowser
- (instancetype)init {
    self = [super init];
    if (self) {
        self.browser = [[ICDeviceBrowser alloc] init];
        self.browser.delegate = self;
        self.browser.browsedDeviceTypeMask = (ICDeviceTypeMask)(ICDeviceTypeMaskCamera | ICDeviceLocationTypeMaskLocal);
        [self.browser start];
    }
    return self;
}
- (void)deviceBrowser:(ICDeviceBrowser *)browser didAddDevice:(ICDevice *)device moreComing:(BOOL)moreComing {}
- (void)deviceBrowser:(ICDeviceBrowser *)browser didRemoveDevice:(ICDevice *)device moreGoing:(BOOL)moreGoing {
    if (device == self.active) [self didRemoveDevice:device];
}
- (void)didRemoveDevice:(ICDevice *)device {
    if (device != self.active) return;
    [self.opening finish:-2 data:nil];
    [self.command finish:-2 data:nil];
    [self.closing finish:0 data:nil];
    device.delegate = nil;
    self.active = nil;
}
- (void)finishOpeningIfReady {
    if (!self.opened || !self.catalogReady || self.opening.finished) return;
    [self.opening finish:0 data:encodeJSON(deviceInfo(self.active))];
}
- (void)device:(ICDevice *)device didOpenSessionWithError:(NSError *)error {
    if (device != self.active) return;
    if (error) {
        [self.opening finish:-1 data:[error.localizedDescription dataUsingEncoding:NSUTF8StringEncoding]];
        device.delegate = nil;
        self.active = nil;
        return;
    }
    self.opened = YES;
    if (self.opening.finished) {
        [device requestCloseSession];
    } else {
        [self finishOpeningIfReady];
    }
}
- (void)device:(ICDevice *)device didCloseSessionWithError:(NSError *)error {
    if (device != self.active) return;
    device.delegate = nil;
    self.active = nil;
    [self.closing finish:0 data:nil];
}
- (void)deviceDidBecomeReadyWithCompleteContentCatalog:(ICCameraDevice *)device {
    if (device != self.active) return;
    self.catalogReady = YES;
    [self finishOpeningIfReady];
}
- (void)cameraDevice:(ICCameraDevice *)camera didAddItems:(NSArray<ICCameraItem *> *)items {}
- (void)cameraDevice:(ICCameraDevice *)camera didRemoveItems:(NSArray<ICCameraItem *> *)items {}
- (void)cameraDevice:(ICCameraDevice *)camera didRenameItems:(NSArray<ICCameraItem *> *)items {}
- (void)cameraDevice:(ICCameraDevice *)camera didReceiveThumbnail:(CGImageRef)thumbnail forItem:(ICCameraItem *)item error:(NSError *)error {}
- (void)cameraDevice:(ICCameraDevice *)camera didReceiveMetadata:(NSDictionary *)metadata forItem:(ICCameraItem *)item error:(NSError *)error {}
- (void)cameraDeviceDidChangeCapability:(ICCameraDevice *)camera {}
- (void)cameraDevice:(ICCameraDevice *)camera didReceivePTPEvent:(NSData *)eventData {}
- (void)cameraDevice:(ICCameraDevice *)camera didCompleteDeleteFilesWithError:(NSError *)error {}
@end

static CaptureBrowser *captureBrowser(void) {
    static CaptureBrowser *browser;
    if (!browser) browser = [[CaptureBrowser alloc] init];
    return browser;
}

static CameraResult waitFor(CaptureRequest *request) {
    if (dispatch_semaphore_wait(request.ready, dispatch_time(DISPATCH_TIME_NOW, 20 * NSEC_PER_SEC)) != 0) {
        return (CameraResult){ .status = -3 };
    }
    NSData *data = request.data;
    CameraResult result = { .length = data.length, .status = request.status };
    if (result.length) {
        result.bytes = malloc(result.length);
        if (!result.bytes) return (CameraResult){ .status = -1 };
        memcpy(result.bytes, data.bytes, result.length);
    }
    return result;
}

CameraResult ci_mac_scan(void) {
    @autoreleasepool {
        CaptureRequest *request = [[CaptureRequest alloc] init];
        dispatch_async(dispatch_get_main_queue(), ^{
            CaptureBrowser *state = captureBrowser();
            dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 1200 * NSEC_PER_MSEC), dispatch_get_main_queue(), ^{
                NSMutableArray *result = [NSMutableArray array];
                for (ICDevice *device in state.browser.devices) {
                    if ([device isKindOfClass:ICCameraDevice.class] && [device.transportType isEqualToString:ICTransportTypeUSB]) {
                        [result addObject:deviceInfo((ICCameraDevice *)device)];
                    }
                }
                [request finish:0 data:encodeJSON(result)];
            });
        });
        return waitFor(request);
    }
}

CameraResult ci_mac_open(const char *identifier) {
    @autoreleasepool {
        CaptureRequest *request = [[CaptureRequest alloc] init];
        NSString *idString = [NSString stringWithUTF8String:identifier];
        dispatch_async(dispatch_get_main_queue(), ^{
            CaptureBrowser *state = captureBrowser();
            if (state.active) {
                [request finish:-1 data:nil];
                return;
            }
            for (ICDevice *device in state.browser.devices) {
                if (![device isKindOfClass:ICCameraDevice.class] || ![device.transportType isEqualToString:ICTransportTypeUSB]) continue;
                ICCameraDevice *camera = (ICCameraDevice *)device;
                if (![deviceID(camera) isEqualToString:idString]) continue;
                if (![camera.capabilities containsObject:ICCameraDeviceCanAcceptPTPCommands]) {
                    [request finish:0 data:encodeJSON(deviceInfo(camera))];
                    return;
                }
                state.active = camera;
                state.opening = request;
                state.opened = NO;
                state.catalogReady = NO;
                camera.delegate = state;
                [camera requestOpenSession];
                dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 14 * NSEC_PER_SEC), dispatch_get_main_queue(), ^{
                    if (!request.finished) {
                        [request finish:-3 data:nil];
                        [camera requestCloseSession];
                    }
                });
                return;
            }
            [request finish:-2 data:nil];
        });
        return waitFor(request);
    }
}

static uint16_t read16(const uint8_t *bytes) {
    return (uint16_t)bytes[0] | ((uint16_t)bytes[1] << 8);
}

static uint32_t read32(const uint8_t *bytes) {
    return (uint32_t)read16(bytes) | ((uint32_t)read16(bytes + 2) << 16);
}

static int32_t responseCode(NSData *packet) {
    const uint8_t *bytes = packet.bytes;
    for (size_t prefix = 0; prefix <= 4; prefix += 4) {
        if (packet.length >= prefix + 12 && packet.length <= prefix + 32 &&
            read32(bytes + prefix) == packet.length - prefix && read16(bytes + prefix + 4) == 3) {
            return read16(bytes + prefix + 6);
        }
    }
    return -4;
}

CameraResult ci_mac_command(const uint8_t *header, size_t headerLength, const uint8_t *outData, size_t outLength) {
    @autoreleasepool {
        CaptureRequest *request = [[CaptureRequest alloc] init];
        NSData *command = [NSData dataWithBytes:header length:headerLength];
        NSData *outgoing = outLength ? [NSData dataWithBytes:outData length:outLength] : nil;
        dispatch_async(dispatch_get_main_queue(), ^{
            CaptureBrowser *state = captureBrowser();
            ICCameraDevice *camera = state.active;
            if (!camera) {
                [request finish:-2 data:nil];
                return;
            }
            state.command = request;
            [camera requestSendPTPCommand:command outData:outgoing completion:^(NSData *response, NSData *data, NSError *error) {
                dispatch_async(dispatch_get_main_queue(), ^{
                    NSData *payload = data;
                    int32_t code = responseCode(response);
                    if (code < 0 && !error) {
                        code = responseCode(data);
                        payload = response;
                    }
                    if (code == 0x2001 && payload.length > 1024 * 1024) code = -4;
                    [request finish:code data:code == 0x2001 ? payload : nil];
                });
            }];
            dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 5 * NSEC_PER_SEC), dispatch_get_main_queue(), ^{
                [request finish:-3 data:nil];
            });
        });
        return waitFor(request);
    }
}

void ci_mac_close(void) {
    @autoreleasepool {
        CaptureRequest *request = [[CaptureRequest alloc] init];
        dispatch_async(dispatch_get_main_queue(), ^{
            CaptureBrowser *state = captureBrowser();
            if (!state.active) {
                [request finish:0 data:nil];
                return;
            }
            state.closing = request;
            [state.active requestCloseSession];
            dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 3 * NSEC_PER_SEC), dispatch_get_main_queue(), ^{
                [request finish:-3 data:nil];
            });
        });
        CameraResult result = waitFor(request);
        free(result.bytes);
    }
}

void ci_mac_free(uint8_t *bytes) {
    free(bytes);
}
