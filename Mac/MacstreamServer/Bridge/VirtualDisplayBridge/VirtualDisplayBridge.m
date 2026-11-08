#import "VirtualDisplayBridge.h"

#import <dispatch/dispatch.h>
#import <objc/message.h>
#import <objc/runtime.h>

#if !__has_feature(objc_arc)
#error "VirtualDisplayBridge.m must be built with ARC (-fobjc-arc, see Package.swift)"
#endif

static NSString *const WMVirtualDisplayErrorDomain = @"Macstream.VirtualDisplay";

@interface WMVirtualDisplayHandle ()
- (instancetype)initWithDisplay:(id)display displayID:(CGDirectDisplayID)displayID;
@property(nonatomic, strong) id display;
@property(nonatomic, readwrite) CGDirectDisplayID displayID;
@end

@implementation WMVirtualDisplayHandle

- (instancetype)initWithDisplay:(id)display displayID:(CGDirectDisplayID)displayID
{
    self = [super init];
    if (self) {
        _display = display;
        _displayID = displayID;
    }
    return self;
}

@end

static NSError *WMVirtualDisplayError(NSInteger code, NSString *message)
{
    return [NSError errorWithDomain:WMVirtualDisplayErrorDomain
                               code:code
                           userInfo:@{NSLocalizedDescriptionKey: message}];
}

// alloc and init* return an object the caller owns. Called through objc_msgSend casts to
// id-returning functions, ARC can't see that and retains it once more, so the object is
// never freed: the CGVirtualDisplay (and so the extra display on the Mac) then stayed until
// the app quit, and a new one couldn't be made. Go through void * and hand that one
// reference to ARC with __bridge_transfer instead.
static void *WMAlloc(Class cls)
{
    return ((void *(*)(void *, SEL))objc_msgSend)((__bridge void *)cls, sel_registerName("alloc"));
}

static id WMAllocInit(Class cls)
{
    void *allocated = WMAlloc(cls);
    return (__bridge_transfer id)((void *(*)(void *, SEL))objc_msgSend)(allocated, sel_registerName("init"));
}

static void WMSetUInt(id object, const char *selectorName, uint32_t value)
{
    ((void (*)(id, SEL, uint32_t))objc_msgSend)(object, sel_registerName(selectorName), value);
}

static void WMSetCGSize(id object, const char *selectorName, CGSize value)
{
    ((void (*)(id, SEL, CGSize))objc_msgSend)(object, sel_registerName(selectorName), value);
}

static void WMSetCGPoint(id object, const char *selectorName, CGPoint value)
{
    ((void (*)(id, SEL, CGPoint))objc_msgSend)(object, sel_registerName(selectorName), value);
}

static void WMSetObject(id object, const char *selectorName, id value)
{
    ((void (*)(id, SEL, id))objc_msgSend)(object, sel_registerName(selectorName), value);
}

bool WMVirtualDisplayIsAvailable(void)
{
    return NSClassFromString(@"CGVirtualDisplay") != Nil &&
           NSClassFromString(@"CGVirtualDisplayDescriptor") != Nil &&
           NSClassFromString(@"CGVirtualDisplaySettings") != Nil &&
           NSClassFromString(@"CGVirtualDisplayMode") != Nil;
}

WMVirtualDisplayHandle *WMVirtualDisplayMake(WMVirtualDisplayOptions options, NSError **error)
{
    Class displayClass = NSClassFromString(@"CGVirtualDisplay");
    Class descriptorClass = NSClassFromString(@"CGVirtualDisplayDescriptor");
    Class settingsClass = NSClassFromString(@"CGVirtualDisplaySettings");
    Class modeClass = NSClassFromString(@"CGVirtualDisplayMode");

    if (!displayClass || !descriptorClass || !settingsClass || !modeClass) {
        if (error) {
            *error = WMVirtualDisplayError(1, @"CGVirtualDisplay runtime classes are not available on this macOS version.");
        }
        return nil;
    }

    if (options.width < 640 || options.height < 360 || options.refreshRate < 24) {
        if (error) {
            *error = WMVirtualDisplayError(2, @"Invalid virtual display mode.");
        }
        return nil;
    }

    NSString *name = options.name ? [NSString stringWithUTF8String:options.name] : @"Macstream";
    uint32_t ppi = options.pixelsPerInch == 0 ? 110 : options.pixelsPerInch;
    CGSize physicalSize = CGSizeMake((double)options.width * 25.4 / (double)ppi,
                                     (double)options.height * 25.4 / (double)ppi);

    id descriptor = WMAllocInit(descriptorClass);
    WMSetObject(descriptor, "setName:", name);
    WMSetUInt(descriptor, "setMaxPixelsWide:", options.width);
    WMSetUInt(descriptor, "setMaxPixelsHigh:", options.height);
    WMSetCGSize(descriptor, "setSizeInMillimeters:", physicalSize);
    WMSetUInt(descriptor, "setVendorID:", 0x574D);
    WMSetUInt(descriptor, "setProductID:", 0x0001);
    WMSetUInt(descriptor, "setSerialNum:", 0x0001);
    WMSetCGPoint(descriptor, "setRedPrimary:", CGPointMake(0.680, 0.320));
    WMSetCGPoint(descriptor, "setGreenPrimary:", CGPointMake(0.265, 0.690));
    WMSetCGPoint(descriptor, "setBluePrimary:", CGPointMake(0.150, 0.060));
    WMSetCGPoint(descriptor, "setWhitePoint:", CGPointMake(0.3127, 0.3290));

    dispatch_queue_t queue = dispatch_get_global_queue(QOS_CLASS_USER_INTERACTIVE, 0);
    WMSetObject(descriptor, "setQueue:", (id)queue);

    id display = (__bridge_transfer id)((void *(*)(void *, SEL, id))objc_msgSend)(
        WMAlloc(displayClass),
        sel_registerName("initWithDescriptor:"),
        descriptor);

    if (!display) {
        if (error) {
            *error = WMVirtualDisplayError(3, @"Failed to create CGVirtualDisplay.");
        }
        return nil;
    }

    uint32_t modeWidth = options.logicalWidth > 0 ? options.logicalWidth : options.width;
    uint32_t modeHeight = options.logicalHeight > 0 ? options.logicalHeight : options.height;
    modeWidth = MAX(modeWidth, 320);
    modeHeight = MAX(modeHeight, 180);
    id mode = (__bridge_transfer id)((void *(*)(void *, SEL, uint32_t, uint32_t, double))objc_msgSend)(
        WMAlloc(modeClass),
        sel_registerName("initWithWidth:height:refreshRate:"),
        modeWidth,
        modeHeight,
        options.refreshRate);

    id settings = WMAllocInit(settingsClass);
    WMSetUInt(settings, "setHiDPI:", options.hiDPI ? 1 : 0);
    WMSetObject(settings, "setModes:", @[mode]);

    BOOL applied = ((BOOL (*)(id, SEL, id))objc_msgSend)(
        display,
        sel_registerName("applySettings:"),
        settings);
    if (!applied) {
        if (error) {
            *error = WMVirtualDisplayError(4, @"CGVirtualDisplay failed to apply display settings.");
        }
        return nil;
    }

    CGDirectDisplayID displayID = ((CGDirectDisplayID (*)(id, SEL))objc_msgSend)(
        display,
        sel_registerName("displayID"));
    if (displayID == 0) {
        if (error) {
            *error = WMVirtualDisplayError(5, @"CGVirtualDisplay returned an invalid display ID.");
        }
        return nil;
    }

    return [[WMVirtualDisplayHandle alloc] initWithDisplay:display displayID:displayID];
}
