#pragma once

#include <stdbool.h>
#include <stdint.h>
#import <CoreGraphics/CoreGraphics.h>
#import <Foundation/Foundation.h>

NS_ASSUME_NONNULL_BEGIN

typedef struct WMVirtualDisplayOptions {
    uint32_t width;
    uint32_t height;
    uint32_t logicalWidth;
    uint32_t logicalHeight;
    double refreshRate;
    uint32_t pixelsPerInch;
    bool hiDPI;
    const char *_Nullable name;
} WMVirtualDisplayOptions;

@interface WMVirtualDisplayHandle : NSObject
@property(nonatomic, readonly) CGDirectDisplayID displayID;
@end

FOUNDATION_EXPORT bool WMVirtualDisplayIsAvailable(void);
FOUNDATION_EXPORT WMVirtualDisplayHandle *_Nullable WMVirtualDisplayMake(WMVirtualDisplayOptions options, NSError **_Nullable error);

NS_ASSUME_NONNULL_END
