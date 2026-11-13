# HCG Nexus Preview — standalone app that hosts the real panel UI without any
# injection. Packaged separately from the tweak; never part of the tweak .deb.
#
#   make -C PreviewHarness package HCG_ENV=rootless|rootful
#
# After installing the .deb yourself, the app icon appears only once the
# icon cache is refreshed (e.g. `uicache -p <path>`); this build never does it.

HCG_ENV ?= rootless
ifeq ($(HCG_ENV),rootless)
  THEOS_PACKAGE_SCHEME = rootless
  TARGET := iphone:clang:16.5:15.0
else
  TARGET := iphone:clang:14.5:14.0
endif
HCG_ARCHS ?= arm64 arm64e
ARCHS = $(HCG_ARCHS)

include $(THEOS)/makefiles/common.mk

APPLICATION_NAME = HCGNexusPreview

ROOT := ..
HCGNexusPreview_FILES = main.m HCGPreviewRootViewController.m HCGPreviewSelfCheck.m \
	$(wildcard $(ROOT)/Sources/Core/*.m) \
	$(wildcard $(ROOT)/Sources/Panel/*.m) \
	$(wildcard $(ROOT)/Sources/Platform/Preview/*.m)
HCGNexusPreview_FRAMEWORKS = UIKit QuartzCore CoreGraphics
HCGNexusPreview_CFLAGS = -fobjc-arc -Wall -Werror \
	-I$(ROOT)/Sources/Core -I$(ROOT)/Sources/Panel -I$(ROOT)/Sources/Platform/Preview -I. \
	-DHCG_PACKAGE_SCHEME=\"preview-$(HCG_ENV)\"
HCGNexusPreview_RESOURCE_DIRS = Resources

include $(THEOS_MAKE_PATH)/application.mk
