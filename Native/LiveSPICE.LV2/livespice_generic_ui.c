#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include <math.h>
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
    char* name;
    char* group;
    char* type;
    double value;
    int positions;
} SchematicControl;

typedef struct {
    GtkWidget* area;
    double value;
    bool dragging;
    double drag_y;
    double drag_value;
} KnobControl;

typedef struct {
    LV2UI_Write_Function write;
    LV2UI_Controller controller;
    LV2UI_Resize* resize;
    LV2_URID_Map* map;
    LV2_URID atom_event_transfer;
    LV2_Atom_Forge forge;
    GtkWidget* root;
    GtkWidget* path_label;
    GtkWidget* controls_box;
    GtkWidget* empty_controls_label;
    char* schematic_path;
    int control_count;
} LiveSpiceGenericUi;

static const char* css_template =
    "#livespice-root {"
    "  background-color: #666;"
    "  background-image: linear-gradient(145deg, rgba(255,255,255,0.55), rgba(255,255,255,0.08) 28%, rgba(0,0,0,0.12) 55%, rgba(255,255,255,0.20)), url('%s');"
    "  background-repeat: repeat;"
    "  border-radius: 8px;"
    "  border: 1px solid rgba(255,255,255,0.45);"
    "  box-shadow: inset 0 1px rgba(255,255,255,0.85), inset 0 -1px rgba(0,0,0,0.35);"
    "}"
    "#livespice-title { color: #111; font-weight: 800; font-size: 15px; text-shadow: 0 1px rgba(255,255,255,0.45); }"
    "#livespice-path { color: #202020; font-weight: 700; text-shadow: 0 1px rgba(255,255,255,0.35); }"
    ".livespice-button {"
    "  color: #111; font-weight: 700; padding: 4px 12px; border-radius: 4px;"
    "  background-image: linear-gradient(#f4f4f4, #b8b8b8 48%, #8f8f8f 52%, #d5d5d5);"
    "  border: 1px solid #4b4b4b; box-shadow: inset 0 1px rgba(255,255,255,0.9), 0 1px rgba(0,0,0,0.25);"
    "}"
    ".livespice-control-card {"
    "  padding: 2px 4px; min-width: 82px; min-height: 86px;"
    "}"
    ".livespice-control-label { color: #101010; font-weight: 800; font-size: 11px; text-shadow: 0 1px rgba(255,255,255,0.45); }"
    ".livespice-combo { color: #111; font-weight: 700; }";

static char* duplicate_range(const char* start, size_t length)
{
    char* copy = (char*)calloc(length + 1, sizeof(char));
    if (copy == NULL)
        return NULL;

    memcpy(copy, start, length);
    return copy;
}

static char* read_attribute(const char* element, const char* name)
{
    char pattern[64];
    snprintf(pattern, sizeof(pattern), "%s=\"", name);

    const char* value = strstr(element, pattern);
    if (value == NULL)
        return NULL;

    value += strlen(pattern);
    const char* end = strchr(value, '"');
    if (end == NULL)
        return NULL;

    return duplicate_range(value, (size_t)(end - value));
}

static bool component_is_type(const char* type, const char* component_name)
{
    return type != NULL && strstr(type, component_name) != NULL;
}

static int switch_positions_from_type(const char* type)
{
    if (component_is_type(type, "SPDT"))
        return 2;
    if (component_is_type(type, "SP3T"))
        return 3;
    if (component_is_type(type, "SP4T"))
        return 4;
    if (component_is_type(type, "SP5T"))
        return 5;
    return 2;
}

static char* control_display_name(const SchematicControl* control)
{
    if (control->group != NULL && control->group[0] != '\0')
        return strdup(control->group);
    if (control->name != NULL && control->name[0] != '\0')
        return strdup(control->name);
    return strdup(control->type != NULL ? control->type : "Control");
}

static void free_schematic_control(SchematicControl* control)
{
    free(control->name);
    free(control->group);
    free(control->type);
}

static bool add_unique_control(GArray* controls, SchematicControl* control)
{
    char* display_name = control_display_name(control);
    if (display_name == NULL)
        return false;

    for (guint i = 0; i < controls->len; i++) {
        SchematicControl* existing = &g_array_index(controls, SchematicControl, i);
        char* existing_name = control_display_name(existing);
        bool duplicate = existing_name != NULL && strcmp(existing_name, display_name) == 0 && strcmp(existing->type, control->type) == 0;
        free(existing_name);
        if (duplicate) {
            free(display_name);
            free_schematic_control(control);
            return true;
        }
    }

    free(display_name);
    g_array_append_val(controls, *control);
    return true;
}

static GArray* read_schematic_controls(const char* path)
{
    GArray* controls = g_array_new(false, false, sizeof(SchematicControl));
    if (path == NULL || path[0] == '\0')
        return controls;

    gchar* contents = NULL;
    gsize length = 0;
    if (!g_file_get_contents(path, &contents, &length, NULL))
        return controls;

    const char* cursor = contents;
    while ((cursor = strstr(cursor, "<Component ")) != NULL) {
        const char* end = strchr(cursor, '>');
        if (end == NULL)
            break;

        char* element = duplicate_range(cursor, (size_t)(end - cursor));
        if (element == NULL)
            break;

        char* component_type = read_attribute(element, "_Type");
        if (component_is_type(component_type, "Potentiometer") || component_is_type(component_type, "VariableResistor")) {
            SchematicControl control = { 0 };
            control.name = read_attribute(element, "Name");
            control.group = read_attribute(element, "Group");
            control.type = strdup("pot");
            char* wipe = read_attribute(element, "Wipe");
            control.value = wipe != NULL ? g_ascii_strtod(wipe, NULL) : 0.5;
            control.positions = 0;
            free(wipe);
            add_unique_control(controls, &control);
        }
        else if (component_is_type(component_type, "SPDT") || component_is_type(component_type, "SP3T") || component_is_type(component_type, "SP4T") || component_is_type(component_type, "SP5T")) {
            SchematicControl control = { 0 };
            control.name = read_attribute(element, "Name");
            control.group = read_attribute(element, "Group");
            control.type = strdup("switch");
            char* position = read_attribute(element, "Position");
            control.value = position != NULL ? g_ascii_strtod(position, NULL) : 0;
            control.positions = switch_positions_from_type(component_type);
            free(position);
            add_unique_control(controls, &control);
        }

        free(component_type);
        free(element);
        cursor = end + 1;
    }

    g_free(contents);
    return controls;
}

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

static void add_css_class(GtkWidget* widget, const char* class_name)
{
    GtkStyleContext* context = gtk_widget_get_style_context(widget);
    gtk_style_context_add_class(context, class_name);
}

static void install_css(const char* bundle_path)
{
    char* texture_path = g_build_filename(bundle_path != NULL ? bundle_path : "", "MetalAlpha.png", NULL);
    char* texture_uri = g_filename_to_uri(texture_path, NULL, NULL);
    char* css = g_strdup_printf(css_template, texture_uri != NULL ? texture_uri : "");
    GtkCssProvider* provider = gtk_css_provider_new();
    gtk_css_provider_load_from_data(provider, css, -1, NULL);
    gtk_style_context_add_provider_for_screen(gdk_screen_get_default(), GTK_STYLE_PROVIDER(provider), GTK_STYLE_PROVIDER_PRIORITY_APPLICATION);
    g_object_unref(provider);
    g_free(css);
    g_free(texture_uri);
    g_free(texture_path);
}

static double clamp_unit(double value)
{
    if (value < 0)
        return 0;
    if (value > 1)
        return 1;
    return value;
}

static void set_label_path(LiveSpiceGenericUi* self, const char* path)
{
    gtk_label_set_text(GTK_LABEL(self->path_label), path != NULL && path[0] != '\0' ? path : "No schematic loaded");
}

static void clear_control_panel(LiveSpiceGenericUi* self)
{
    GList* children = gtk_container_get_children(GTK_CONTAINER(self->controls_box));
    for (GList* child = children; child != NULL; child = child->next)
        gtk_widget_destroy(GTK_WIDGET(child->data));
    g_list_free(children);
    self->control_count = 0;
}

static void request_ui_size(LiveSpiceGenericUi* self)
{
    if (self->resize == NULL || self->resize->ui_resize == NULL)
        return;

    const int base_width = 470;
    const int control_width = 96;
    const int max_width = 920;
    int width = base_width + (self->control_count * control_width);
    if (width < base_width)
        width = base_width;
    if (width > max_width)
        width = max_width;

    int height = self->control_count > 0 ? 190 : 145;
    self->resize->ui_resize(self->resize->handle, width, height);
}

static GtkWidget* create_control_label(const char* text)
{
    GtkWidget* label = gtk_label_new(text);
    add_css_class(label, "livespice-control-label");
    gtk_widget_set_halign(label, GTK_ALIGN_START);
    gtk_label_set_ellipsize(GTK_LABEL(label), PANGO_ELLIPSIZE_END);
    return label;
}

static gboolean draw_knob(GtkWidget* widget, cairo_t* cr, gpointer data)
{
    (void)widget;
    KnobControl* knob = (KnobControl*)data;
    GtkAllocation allocation;
    gtk_widget_get_allocation(knob->area, &allocation);

    double size = allocation.width < allocation.height ? allocation.width : allocation.height;
    double center = size / 2.0;
    double radius = (size / 2.0) - 5.0;
    double value = clamp_unit(knob->value);

    cairo_pattern_t* shadow = cairo_pattern_create_radial(center + 3, center + 5, radius * 0.15, center + 3, center + 5, radius);
    cairo_pattern_add_color_stop_rgba(shadow, 0, 0, 0, 0, 0.20);
    cairo_pattern_add_color_stop_rgba(shadow, 1, 0, 0, 0, 0.00);
    cairo_set_source(cr, shadow);
    cairo_arc(cr, center + 3, center + 5, radius, 0, 2 * G_PI);
    cairo_fill(cr);
    cairo_pattern_destroy(shadow);

    cairo_pattern_t* body = cairo_pattern_create_radial(center - radius * 0.35, center - radius * 0.45, radius * 0.1, center, center, radius);
    cairo_pattern_add_color_stop_rgb(body, 0, 0.96, 0.96, 0.93);
    cairo_pattern_add_color_stop_rgb(body, 0.42, 0.58, 0.58, 0.56);
    cairo_pattern_add_color_stop_rgb(body, 1, 0.14, 0.14, 0.14);
    cairo_set_source(cr, body);
    cairo_arc(cr, center, center, radius, 0, 2 * G_PI);
    cairo_fill_preserve(cr);
    cairo_pattern_destroy(body);

    cairo_set_source_rgb(cr, 0.07, 0.07, 0.07);
    cairo_set_line_width(cr, 1.2);
    cairo_stroke(cr);

    cairo_pattern_t* cap = cairo_pattern_create_linear(center, center - radius, center, center + radius);
    cairo_pattern_add_color_stop_rgba(cap, 0, 1, 1, 1, 0.48);
    cairo_pattern_add_color_stop_rgba(cap, 0.45, 1, 1, 1, 0.06);
    cairo_pattern_add_color_stop_rgba(cap, 1, 0, 0, 0, 0.22);
    cairo_set_source(cr, cap);
    cairo_arc(cr, center, center, radius - 3, 0, 2 * G_PI);
    cairo_fill(cr);
    cairo_pattern_destroy(cap);

    double angle = (-135.0 + (270.0 * value)) * G_PI / 180.0;
    double indicator_inner = radius * 0.18;
    double indicator_outer = radius * 0.78;
    cairo_set_source_rgb(cr, 0.02, 0.02, 0.02);
    cairo_set_line_width(cr, 4.0);
    cairo_set_line_cap(cr, CAIRO_LINE_CAP_ROUND);
    cairo_move_to(cr, center + cos(angle) * indicator_inner, center + sin(angle) * indicator_inner);
    cairo_line_to(cr, center + cos(angle) * indicator_outer, center + sin(angle) * indicator_outer);
    cairo_stroke(cr);

    cairo_set_source_rgba(cr, 1, 1, 1, 0.85);
    cairo_set_line_width(cr, 1.2);
    cairo_move_to(cr, center + cos(angle) * indicator_inner, center + sin(angle) * indicator_inner);
    cairo_line_to(cr, center + cos(angle) * indicator_outer, center + sin(angle) * indicator_outer);
    cairo_stroke(cr);

    return false;
}

static gboolean knob_button_press(GtkWidget* widget, GdkEventButton* event, gpointer data)
{
    (void)widget;
    KnobControl* knob = (KnobControl*)data;
    if (event->button != GDK_BUTTON_PRIMARY)
        return false;

    knob->dragging = true;
    knob->drag_y = event->y_root;
    knob->drag_value = knob->value;
    return true;
}

static gboolean knob_button_release(GtkWidget* widget, GdkEventButton* event, gpointer data)
{
    (void)widget;
    (void)event;
    ((KnobControl*)data)->dragging = false;
    return true;
}

static gboolean knob_motion(GtkWidget* widget, GdkEventMotion* event, gpointer data)
{
    KnobControl* knob = (KnobControl*)data;
    if (!knob->dragging)
        return false;

    knob->value = clamp_unit(knob->drag_value + ((knob->drag_y - event->y_root) / 120.0));
    gtk_widget_queue_draw(widget);
    return true;
}

static gboolean knob_scroll(GtkWidget* widget, GdkEventScroll* event, gpointer data)
{
    KnobControl* knob = (KnobControl*)data;
    double delta = event->direction == GDK_SCROLL_UP ? 0.025 : -0.025;
    knob->value = clamp_unit(knob->value + delta);
    gtk_widget_queue_draw(widget);
    return true;
}

static void free_knob_control(gpointer data)
{
    free(data);
}

static GtkWidget* create_knob(double value)
{
    KnobControl* knob = (KnobControl*)calloc(1, sizeof(KnobControl));
    knob->value = clamp_unit(value);
    knob->area = gtk_drawing_area_new();
    gtk_widget_set_size_request(knob->area, 68, 68);
    gtk_widget_add_events(knob->area, GDK_BUTTON_PRESS_MASK | GDK_BUTTON_RELEASE_MASK | GDK_POINTER_MOTION_MASK | GDK_SCROLL_MASK);
    g_signal_connect(knob->area, "draw", G_CALLBACK(draw_knob), knob);
    g_signal_connect(knob->area, "button-press-event", G_CALLBACK(knob_button_press), knob);
    g_signal_connect(knob->area, "button-release-event", G_CALLBACK(knob_button_release), knob);
    g_signal_connect(knob->area, "motion-notify-event", G_CALLBACK(knob_motion), knob);
    g_signal_connect(knob->area, "scroll-event", G_CALLBACK(knob_scroll), knob);
    g_object_set_data_full(G_OBJECT(knob->area), "livespice-knob", knob, free_knob_control);
    return knob->area;
}

static GtkWidget* create_pot_control(const SchematicControl* control)
{
    GtkWidget* box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 4);
    add_css_class(box, "livespice-control-card");
    gtk_widget_set_size_request(box, 86, 90);
    gtk_widget_set_valign(box, GTK_ALIGN_START);
    char* name = control_display_name(control);
    gtk_box_pack_start(GTK_BOX(box), create_control_label(name != NULL ? name : "Pot"), false, false, 0);

    GtkWidget* knob = create_knob(control->value);
    gtk_widget_set_halign(knob, GTK_ALIGN_CENTER);
    gtk_box_pack_start(GTK_BOX(box), knob, false, false, 0);
    free(name);
    return box;
}

static GtkWidget* create_switch_control(const SchematicControl* control)
{
    GtkWidget* box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 4);
    add_css_class(box, "livespice-control-card");
    gtk_widget_set_size_request(box, 86, 90);
    gtk_widget_set_valign(box, GTK_ALIGN_START);
    char* name = control_display_name(control);
    gtk_box_pack_start(GTK_BOX(box), create_control_label(name != NULL ? name : "Switch"), false, false, 0);

    GtkWidget* combo = gtk_combo_box_text_new();
    for (int i = 0; i < control->positions; i++) {
        char text[16];
        snprintf(text, sizeof(text), "%d", i);
        gtk_combo_box_text_append_text(GTK_COMBO_BOX_TEXT(combo), text);
    }
    gtk_combo_box_set_active(GTK_COMBO_BOX(combo), (int)control->value);
    add_css_class(combo, "livespice-combo");
    gtk_box_pack_start(GTK_BOX(box), combo, false, false, 0);
    free(name);
    return box;
}

static void rebuild_control_panel(LiveSpiceGenericUi* self, const char* path)
{
    clear_control_panel(self);

    GArray* controls = read_schematic_controls(path);
    if (controls->len == 0) {
        self->empty_controls_label = create_control_label(path != NULL && path[0] != '\0' ? "No schematic controls found" : "Load a schematic to show controls");
        gtk_box_pack_start(GTK_BOX(self->controls_box), self->empty_controls_label, false, false, 0);
    }
    else {
        for (guint i = 0; i < controls->len; i++) {
            SchematicControl* control = &g_array_index(controls, SchematicControl, i);
            GtkWidget* widget = strcmp(control->type, "switch") == 0 ? create_switch_control(control) : create_pot_control(control);
            gtk_box_pack_start(GTK_BOX(self->controls_box), widget, false, false, 0);
        }
    }
    self->control_count = (int)controls->len;

    for (guint i = 0; i < controls->len; i++)
        free_schematic_control(&g_array_index(controls, SchematicControl, i));
    g_array_free(controls, true);
    gtk_widget_show_all(self->controls_box);
    request_ui_size(self);
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
    rebuild_control_panel(self, copy);
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
        else if (strcmp((*feature)->URI, LV2_UI__resize) == 0)
            self->resize = (LV2UI_Resize*)(*feature)->data;
    }

    if (self->map != NULL) {
        self->atom_event_transfer = self->map->map(self->map->handle, LV2_ATOM__eventTransfer);
        lv2_atom_forge_init(&self->forge, self->map);
    }

    gtk_init_once();
    install_css(bundle_path);

    self->root = gtk_box_new(GTK_ORIENTATION_VERTICAL, 8);
    gtk_widget_set_name(self->root, "livespice-root");
    gtk_container_set_border_width(GTK_CONTAINER(self->root), 10);

    GtkWidget* title = gtk_label_new("LiveSPICE Generic");
    gtk_widget_set_name(title, "livespice-title");
    gtk_widget_set_halign(title, GTK_ALIGN_START);
    gtk_box_pack_start(GTK_BOX(self->root), title, false, false, 0);

    self->path_label = gtk_label_new("No schematic loaded");
    gtk_widget_set_name(self->path_label, "livespice-path");
    gtk_label_set_ellipsize(GTK_LABEL(self->path_label), PANGO_ELLIPSIZE_MIDDLE);
    gtk_widget_set_halign(self->path_label, GTK_ALIGN_START);
    gtk_box_pack_start(GTK_BOX(self->root), self->path_label, false, false, 0);

    GtkWidget* controls = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 8);
    GtkWidget* load_button = gtk_button_new_with_label("Load Schematic");
    GtkWidget* clear_button = gtk_button_new_with_label("Clear");
    add_css_class(load_button, "livespice-button");
    add_css_class(clear_button, "livespice-button");
    gtk_box_pack_start(GTK_BOX(controls), load_button, false, false, 0);
    gtk_box_pack_start(GTK_BOX(controls), clear_button, false, false, 0);
    gtk_box_pack_start(GTK_BOX(self->root), controls, false, false, 0);

    GtkWidget* scroll = gtk_scrolled_window_new(NULL, NULL);
    gtk_scrolled_window_set_policy(GTK_SCROLLED_WINDOW(scroll), GTK_POLICY_AUTOMATIC, GTK_POLICY_NEVER);
    gtk_widget_set_size_request(scroll, 320, 112);
    gtk_widget_set_valign(scroll, GTK_ALIGN_START);
    self->controls_box = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 10);
    gtk_container_add(GTK_CONTAINER(scroll), self->controls_box);
    gtk_box_pack_start(GTK_BOX(self->root), scroll, false, false, 0);
    rebuild_control_panel(self, "");

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

#ifdef LIVESPICE_UI_SMOKE
static uint32_t smoke_map_uri(LV2_URID_Map_Handle handle, const char* uri)
{
    (void)handle;
    if (strcmp(uri, LV2_ATOM__eventTransfer) == 0)
        return 1;
    if (strcmp(uri, LV2_ATOM__Path) == 0)
        return 2;
    if (strcmp(uri, LV2_ATOM__String) == 0)
        return 3;
    return 100;
}

static void smoke_write(LV2UI_Controller controller, uint32_t port_index, uint32_t buffer_size, uint32_t port_protocol, const void* buffer)
{
    (void)controller;
    (void)port_index;
    (void)buffer_size;
    (void)port_protocol;
    (void)buffer;
}

int main(int argc, char** argv)
{
    if (argc < 4)
        return 2;

    gtk_init(&argc, &argv);

    LV2_URID_Map map = { NULL, smoke_map_uri };
    LV2_Feature map_feature = { LV2_URID__map, &map };
    const LV2_Feature* features[] = { &map_feature, NULL };
    LV2UI_Widget widget = NULL;
    LiveSpiceGenericUi* ui = (LiveSpiceGenericUi*)instantiate(&descriptor, LIVESPICE_GENERIC_URI, argv[1], smoke_write, NULL, &widget, features);
    if (ui == NULL || widget == NULL)
        return 3;

    set_schematic_path(ui, argv[2]);

    GtkWidget* window = gtk_offscreen_window_new();
    gtk_container_add(GTK_CONTAINER(window), GTK_WIDGET(widget));
    gtk_widget_set_size_request(window, 760, 190);
    gtk_widget_show_all(window);

    while (gtk_events_pending())
        gtk_main_iteration();

    GdkPixbuf* pixbuf = gtk_offscreen_window_get_pixbuf(GTK_OFFSCREEN_WINDOW(window));
    if (pixbuf == NULL)
        return 4;

    gboolean ok = gdk_pixbuf_save(pixbuf, argv[3], "png", NULL, NULL);
    g_object_unref(pixbuf);
    cleanup((LV2UI_Handle)ui);
    gtk_widget_destroy(window);
    return ok ? 0 : 5;
}
#endif