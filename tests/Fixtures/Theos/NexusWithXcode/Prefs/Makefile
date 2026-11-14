include $(THEOS)/makefiles/common.mk

BUNDLE_NAME = HCGNexusPrefs

ROOT := ..
HCGNexusPrefs_FILES = HCGNexusRootListController.m \
	$(wildcard $(ROOT)/Sources/Core/*.m) \
	$(wildcard $(ROOT)/Sources/Panel/*.m) \
	$(ROOT)/Sources/Platform/SpringBoard/HCGPreferencesKeyValueStore.m
HCGNexusPrefs_INSTALL_PATH = /Library/PreferenceBundles
HCGNexusPrefs_FRAMEWORKS = UIKit QuartzCore CoreFoundation
HCGNexusPrefs_PRIVATE_FRAMEWORKS = Preferences
HCGNexusPrefs_CFLAGS = -fobjc-arc -Wall -Werror \
	-I$(ROOT)/Sources/Core -I$(ROOT)/Sources/Panel -I$(ROOT)/Sources/Platform/SpringBoard \
	-DHCG_PACKAGE_SCHEME=\"$(HCG_ENV)\"

include $(THEOS_MAKE_PATH)/bundle.mk

internal-stage::
	$(ECHO_NOTHING)mkdir -p $(THEOS_STAGING_DIR)/Library/PreferenceLoader/Preferences$(ECHO_END)
	$(ECHO_NOTHING)cp entry.plist $(THEOS_STAGING_DIR)/Library/PreferenceLoader/Preferences/HCGNexusPrefs.plist$(ECHO_END)
