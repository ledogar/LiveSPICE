#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include <gtk/gtk.h>

#include <lv2/atom/atom.h>
#include <lv2/atom/forge.h>
#include <lv2/core/lv2.h>
#include <lv2/ui/ui.h>
#include <lv2/urid/urid.h>

#define LIVESPICE_GENERIC_URI "https://livespice.org/plugins/generic"
#define LIVESPICE_GENERIC_UI_URI "https://livespice.org/plugins/generic#gtk3-ui"

typedef enum {
    PORT_INPUT = 0,
    PORT_OUTPUT = 1,
    PORT_CONTROL_EVENTS = 2
} PortIndex;

typedef struct {
    LV2UI_Write_Function write;
    LV2UI_Controller controller;
    LV2_URID_Map* map;
    LV2_URID atom_event_transfer;
    LV2_Atom_Forge forge;
    GtkWidget* root;
    GtkWidget* path_label;
    char* schematic_path;
} LiveSpiceGenericUi;

static void gtk_init_once(void)
{
    static bool initialized = false;
    if (!initialized) {
        int argc = 0;
        char** argv = NULL;
        gtk_init(&argc, &argv);
        initialized = true;
    }
}

static void set_label_path(LiveSpiceGenericUi* self, const char* path)
{
    gtk_label_set_text(GTK_LABEL(self->path_label), path != NULL && path[0] != '\0' ? path : "No schematic loaded");
}

static void send_schematic_path(LiveSpiceGenericUi* self, const char* path)
{
    if (self->map == NULL || path == NULL)
        return;

    uint8_t buffer[4096];
    lv2_atom_forge_set_buffer(&self->forge, buffer, sizeof(buffer));
    LV2_Atom_Forge_Ref ref = lv2_atom_forge_path(&self->forge, path, (uint32_t)strlen(path) + 1);
    if (ref == 0)
        return;

    LV2_Atom* atom = lv2_atom_forge_deref(&self->forge, ref);
    self->write(self->controller, PORT_CONTROL_EVENTS, lv2_atom_total_size(atom), self->atom_event_transfer, atom);
}

static void set_schematic_path(LiveSpiceGenericUi* self, const char* path)
{
    char* copy = strdup(path);
    if (copy == NULL)
        return;

    free(self->schematic_path);
    self->schematic_path = copy;
    set_label_path(self, copy);
    send_schematic_path(self, copy);
}

static void load_schematic_clicked(GtkButton* button, gpointer data)
{
    (void)button;
    LiveSpiceGenericUi* self = (LiveSpiceGenericUi*)data;

    GtkWidget* dialog = gtk_file_chooser_dialog_new(
        "Load LiveSPICE Schematic",
        NULL,
        GTK_FILE_CHOOSER_ACTION_OPEN,
        "_Cancel",
        GTK_RESPONSE_CANCEL,
        "_Open",
        GTK_RESPONSE_ACCEPT,
        NULL);

    GtkFileFilter* filter = gtk_file_filter_new();
    gtk_file_filter_set_name(filter, "LiveSPICE schematics");
    gtk_file_filter_add_pattern(filter, "*.schx");
    gtk_file_chooser_add_filter(GTK_FILE_CHOOSER(dialog), filter);

    if (gtk_dialog_run(GTK_DIALOG(dialog)) == GTK_RESPONSE_ACCEPT) {
        char* filename = gtk_file_chooser_get_filename(GTK_FILE_CHOOSER(dialog));
        if (filename != NULL) {
            set_schematic_path(self, filename);
            g_free(filename);
        }
    }

    gtk_widget_destroy(dialog);
}

static void clear_schematic_clicked(GtkButton* button, gpointer data)
{
    (void)button;
    set_schematic_path((LiveSpiceGenericUi*)data, "");
}

static LV2UI_Handle instantiate(
    const LV2UI_Descriptor* descriptor,
    const char* plugin_uri,
    const char* bundle_path,
    LV2UI_Write_Function write_function,
    LV2UI_Controller controller,
    LV2UI_Widget* widget,
    const LV2_Feature* const* features)
{
    (void)descriptor;
    (void)bundle_path;

    if (strcmp(plugin_uri, LIVESPICE_GENERIC_URI) != 0)
        return NULL;

    LiveSpiceGenericUi* self = (LiveSpiceGenericUi*)calloc(1, sizeof(LiveSpiceGenericUi));
    if (self == NULL)
        return NULL;

    self->write = write_function;
    self->controller = controller;

    for (const LV2_Feature* const* feature = features; feature != NULL && *feature != NULL; feature++) {
        if (strcmp((*feature)->URI, LV2_URID__map) == 0)
            self->map = (LV2_URID_Map*)(*feature)->data;
    }

    if (self->map != NULL) {
        self->atom_event_transfer = self->map->map(self->map->handle, LV2_ATOM__eventTransfer);
        lv2_atom_forge_init(&self->forge, self->map);
    }

    gtk_init_once();

    self->root = gtk_box_new(GTK_ORIENTATION_VERTICAL, 8);
    gtk_container_set_border_width(GTK_CONTAINER(self->root), 10);

    GtkWidget* title = gtk_label_new("LiveSPICE Generic");
    gtk_widget_set_halign(title, GTK_ALIGN_START);
    gtk_box_pack_start(GTK_BOX(self->root), title, false, false, 0);

    self->path_label = gtk_label_new("No schematic loaded");
    gtk_label_set_ellipsize(GTK_LABEL(self->path_label), PANGO_ELLIPSIZE_MIDDLE);
    gtk_widget_set_halign(self->path_label, GTK_ALIGN_START);
    gtk_box_pack_start(GTK_BOX(self->root), self->path_label, false, false, 0);

    GtkWidget* controls = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 8);
    GtkWidget* load_button = gtk_button_new_with_label("Load Schematic");
    GtkWidget* clear_button = gtk_button_new_with_label("Clear");
    gtk_box_pack_start(GTK_BOX(controls), load_button, false, false, 0);
    gtk_box_pack_start(GTK_BOX(controls), clear_button, false, false, 0);
    gtk_box_pack_start(GTK_BOX(self->root), controls, false, false, 0);

    g_signal_connect(load_button, "clicked", G_CALLBACK(load_schematic_clicked), self);
    g_signal_connect(clear_button, "clicked", G_CALLBACK(clear_schematic_clicked), self);

    gtk_widget_show_all(self->root);
    *widget = self->root;
    return (LV2UI_Handle)self;
}

static void cleanup(LV2UI_Handle ui)
{
    LiveSpiceGenericUi* self = (LiveSpiceGenericUi*)ui;
    free(self->schematic_path);
    free(self);
}

static void port_event(LV2UI_Handle ui, uint32_t port_index, uint32_t buffer_size, uint32_t format, const void* buffer)
{
    (void)ui;
    (void)port_index;
    (void)buffer_size;
    (void)format;
    (void)buffer;
}

static const void* extension_data(const char* uri)
{
    (void)uri;
    return NULL;
}

static const LV2UI_Descriptor descriptor = {
    LIVESPICE_GENERIC_UI_URI,
    instantiate,
    cleanup,
    port_event,
    extension_data
};

LV2_SYMBOL_EXPORT const LV2UI_Descriptor* lv2ui_descriptor(uint32_t index)
{
    return index == 0 ? &descriptor : NULL;
}