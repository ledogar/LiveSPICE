CC ?= gcc
CFLAGS ?= -O3 -fPIC -Wall -Wextra -Werror
LDFLAGS ?= -shared
GTK3_CFLAGS := $(shell pkg-config --cflags gtk+-3.0)
GTK3_LIBS := $(shell pkg-config --libs gtk+-3.0)
BUNDLE := build/LiveSPICE.lv2
MXR_PLUGIN := $(BUNDLE)/livespice_mxr_phase90.so
GENERIC_PLUGIN := $(BUNDLE)/livespice_generic.so
GENERIC_UI := $(BUNDLE)/livespice_generic_ui.so
TTL := $(BUNDLE)/manifest.ttl $(BUNDLE)/livespice_mxr_phase90.ttl $(BUNDLE)/livespice_generic.ttl
PREFIX ?= $(HOME)/.lv2

.PHONY: all install clean test

all: $(MXR_PLUGIN) $(GENERIC_PLUGIN) $(GENERIC_UI) $(TTL)

$(BUNDLE):
	mkdir -p $(BUNDLE)

$(MXR_PLUGIN): livespice_mxr_phase90.c | $(BUNDLE)
	$(CC) $(CFLAGS) $< $(LDFLAGS) -lm -o $@

$(GENERIC_PLUGIN): livespice_generic.c | $(BUNDLE)
	$(CC) $(CFLAGS) $< $(LDFLAGS) -lm -o $@

$(GENERIC_UI): livespice_generic_ui.c | $(BUNDLE)
	$(CC) $(CFLAGS) $(GTK3_CFLAGS) $< $(LDFLAGS) $(GTK3_LIBS) -o $@

$(BUNDLE)/manifest.ttl: manifest.ttl | $(BUNDLE)
	cp $< $@

$(BUNDLE)/livespice_mxr_phase90.ttl: livespice_mxr_phase90.ttl | $(BUNDLE)
	cp $< $@

$(BUNDLE)/livespice_generic.ttl: livespice_generic.ttl | $(BUNDLE)
	cp $< $@

install: all
	mkdir -p $(PREFIX)
	rm -rf $(PREFIX)/LiveSPICE-MXR-Phase90.lv2
	cp -r $(BUNDLE) $(PREFIX)/

test: all
	LV2_PATH=$(CURDIR)/build lv2ls | grep -F https://livespice.org/plugins/mxr-phase90
	LV2_PATH=$(CURDIR)/build lv2ls | grep -F https://livespice.org/plugins/generic

clean:
	rm -rf build
