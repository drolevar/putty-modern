/*
 * configbox.c - PuTTY's settings dialog, with the drawing left to the host.
 *
 * config.c describes the whole configuration box as a tree of groups of controls (struct controlbox)
 * and gives each control a handler. A front end implements the dlg_* functions the handlers call to read
 * and write the controls, and calls the handlers when the user acts. PuTTY's own Windows version
 * (windows/dialog.c, controls.c) does that with Win32 controls; this one keeps the state of every control
 * itself and reports changes to the host, which draws them (a XAML page in the app).
 *
 * The state of a control lives here, not in the host: handlers read it back at once (for example the
 * list of saved sessions) and the host may not even have drawn the page yet. The host is told what
 * changed ("changed(id)") and reads the new state through the pe_config_get_* functions.
 *
 * Everything runs on the engine thread. A handler may end the dialog ("Open") and the host may answer by
 * closing it while PuTTY's handler is still on the stack, so teardown is deferred while anything is running.
 */

#include <string.h>

#include "putty.h"
#include "dialog.h"
#include "storage.h"
#include "putty_engine.h"
#include "engine_internal.h"

typedef struct pe_list_item {
    char *text;                 /* UTF-8 */
    int id;
    bool selected;
} pe_list_item;

/* The state of one control. Which fields matter depends on its type. */
typedef struct pe_ctrl {
    dlgcontrol *ctrl;
    int32_t group;
    char *text;                 /* an edit box's text, a text line, and the current label */
    bool label_changed;         /* `text` of a label-bearing control was replaced by dlg_label_change */
    bool checked;
    int radio;                  /* the selected button, or -1 */
    pe_list_item *items;
    int nitems;
    size_t itemsize;
    Filename *file;
    FontSpec *font;
    bool colour_ok;
    int colour_r, colour_g, colour_b;
} pe_ctrl;

/*
 * PuTTY passes a `dlgparam *` to every handler and dlg_* function and never looks inside it. The Windows
 * front end defines struct dlgparam in windows/controls.h (which putty.h pulls in), so we cannot define our
 * own; instead the dialog object itself is what a dlgparam pointer points to, by a cast.
 */
#define CFG(dp) ((pe_config *)(dp))
#define DLGPARAM(c) ((dlgparam *)(c))

struct pe_config {
    struct controlbox *box;
    Conf *conf;
    pe_ctrl *ctrls;
    int nctrls;
    int ngroups;
    pe_config_events events;
    void *user;
    char *page;                 /* the page the host shows ("Session"), or NULL */
    int focus[2];               /* the control that has the focus, and the one before it; -1 for none */
    pe_session *session;        /* a box opened on a running session (Change Settings), else NULL */
    uint64_t session_serial;
    bool midsession;
    int busy;                   /* handlers and host callbacks currently on the stack */
    bool closing;
    pe_config *next;
};

static pe_config *live_configs;

static bool config_is_live(const pe_config *c)
{
    for (const pe_config *p = live_configs; p; p = p->next) {
        if (p == c)
            return true;
    }
    return false;
}

/* ----- Calling the host -------------------------------------------------------------------- */

#define HOST_EVENT(c, fn, ...)                                      \
    do {                                                            \
        if (!(c)->closing && (c)->events.fn) {                      \
            (c)->busy++;                                            \
            (c)->events.fn((c)->user, __VA_ARGS__);                 \
            (c)->busy--;                                            \
        }                                                           \
    } while (0)

#define HOST_EVENT_NOARGS(c, fn)                                   \
    do {                                                            \
        if (!(c)->closing && (c)->events.fn) {                      \
            (c)->busy++;                                            \
            (c)->events.fn((c)->user);                              \
            (c)->busy--;                                            \
        }                                                           \
    } while (0)

/* ----- Finding controls -------------------------------------------------------------------- */

static pe_ctrl *find_ctrl(dlgparam *dp, dlgcontrol *ctrl)
{
    pe_config *c = CFG(dp);
    for (int i = 0; i < c->nctrls; i++) {
        if (c->ctrls[i].ctrl == ctrl)
            return &c->ctrls[i];
    }
    return NULL;
}

static int ctrl_id(pe_config *c, const pe_ctrl *k)
{
    return (int)(k - c->ctrls);
}

static pe_ctrl *ctrl_by_id(pe_config *c, int32_t id)
{
    return (id >= 0 && id < c->nctrls) ? &c->ctrls[id] : NULL;
}

static void notify_changed(pe_config *c, pe_ctrl *k)
{
    HOST_EVENT(c, changed, (int32_t)ctrl_id(c, k));
}

/* Runs PuTTY's handler for a control, keeping the dialog alive while it does. */
static void run_handler(pe_config *c, pe_ctrl *k, int event)
{
    if (!k->ctrl->handler)
        return;
    c->busy++;
    k->ctrl->handler(k->ctrl, DLGPARAM(c), c->conf, event);
    c->busy--;
}

/* ----- Building the dialog ----------------------------------------------------------------- */

static void ctrls_init(pe_config *c)
{
    int total = 0;
    for (size_t s = 0; s < c->box->nctrlsets; s++)
        total += (int)c->box->ctrlsets[s]->ncontrols;
    c->ctrls = snewn(total ? total : 1, pe_ctrl);
    memset(c->ctrls, 0, sizeof(pe_ctrl) * (total ? total : 1));
    c->nctrls = 0;
    for (size_t s = 0; s < c->box->nctrlsets; s++) {
        struct controlset *set = c->box->ctrlsets[s];
        ctrlset_normalise_aligns(set);
        for (size_t i = 0; i < set->ncontrols; i++) {
            pe_ctrl *k = &c->ctrls[c->nctrls++];
            k->ctrl = set->ctrls[i];
            k->group = (int32_t)s;
            k->radio = -1;
            k->text = dupstr("");
        }
    }
    c->ngroups = (int)c->box->nctrlsets;
}

static void list_free(pe_ctrl *k)
{
    for (int i = 0; i < k->nitems; i++)
        sfree(k->items[i].text);
    sfree(k->items);
    k->items = NULL;
    k->nitems = 0;
    k->itemsize = 0;
}

static void config_destroy(pe_config *c)
{
    for (pe_config **pp = &live_configs; *pp; pp = &(*pp)->next) {
        if (*pp == c) {
            *pp = c->next;
            break;
        }
    }
    for (int i = 0; i < c->nctrls; i++) {
        pe_ctrl *k = &c->ctrls[i];
        sfree(k->text);
        list_free(k);
        if (k->file)
            filename_free(k->file);
        if (k->font)
            fontspec_free(k->font);
    }
    sfree(c->ctrls);
    if (c->box)
        ctrl_free_box(c->box);
    if (c->conf)
        conf_free(c->conf);
    sfree(c->page);
    sfree(c);
}

static void config_destroy_callback(void *ctx)
{
    pe_config *c = (pe_config *)ctx;
    if (c->busy > 0) {          /* still on a stack somewhere: try again once that has unwound */
        queue_toplevel_callback(config_destroy_callback, c);
        return;
    }
    config_destroy(c);
}

void pe_configs_close_all(void)
{
    while (live_configs) {
        pe_config *c = live_configs;
        c->busy = 0;            /* the engine is shutting down: nothing can still be running */
        config_destroy(c);
    }
}

static pe_config *config_create(const pe_config_events *events, void *user)
{
    pe_config *c = snew(pe_config);
    memset(c, 0, sizeof(*c));
    c->events = *events;
    c->user = user;
    c->focus[0] = c->focus[1] = -1;
    return c;
}

/* Builds the state of every control from the box and makes the dialog known; the box and the Conf must be set. */
static void config_register(pe_config *c)
{
    ctrls_init(c);
    c->next = live_configs;
    live_configs = c;
}

PE_API int32_t pe_config_open(uint32_t flags, const pe_config_events *events, void *user, pe_config **out)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!events || !out)
        return PE_ERR_FAILED;

    pe_config *c = config_create(events, user);
    c->conf = conf_new();
    if (flags & PE_OPT_SAVED_DEFAULTS)
        do_defaults(NULL, c->conf);
    else
        load_open_settings(NULL, c->conf);

    c->box = ctrl_new_box();
    setup_config_box(c->box, false, 0, 0);
    config_register(c);
    *out = c;
    return PE_OK;
}

/*
 * PuTTY's Change Settings: the same box on a copy of a running session's settings, with what PuTTY leaves out
 * mid-session left out (setup_config_box's midsession mode: no host name, no Open; the button says Apply).
 * pe_config_apply then makes the edited copy the session's.
 */
PE_API int32_t pe_config_open_for_session(pe_session *session, const pe_config_events *events, void *user, pe_config **out)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!session || !events || !out)
        return PE_ERR_FAILED;

    Conf *conf = NULL;
    int protocol = 0, cfginfo = 0;
    uint64_t serial = 0;
    rc = pe_session_config_source(session, &conf, &protocol, &cfginfo, &serial);
    if (rc != PE_OK)
        return rc;

    pe_config *c = config_create(events, user);
    c->conf = conf;
    c->session = session;
    c->session_serial = serial;
    c->midsession = true;
    c->box = ctrl_new_box();
    setup_config_box(c->box, true, protocol, cfginfo);
    config_register(c);
    *out = c;
    return PE_OK;
}

PE_API int32_t pe_config_apply(pe_config *c)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!config_is_live(c) || c->closing || !c->midsession)
        return PE_ERR_STATE;
    return pe_session_apply_conf(c->session, c->session_serial, c->conf);
}

/* ----- Describing the controls to the host ----------------------------------------------- */

static int32_t type_of(int ctrl_type)
{
    switch (ctrl_type) {
      case CTRL_TEXT:        return PE_CTRL_TEXT;
      case CTRL_EDITBOX:     return PE_CTRL_EDITBOX;
      case CTRL_RADIO:       return PE_CTRL_RADIO;
      case CTRL_CHECKBOX:    return PE_CTRL_CHECKBOX;
      case CTRL_BUTTON:      return PE_CTRL_BUTTON;
      case CTRL_LISTBOX:     return PE_CTRL_LISTBOX;
      case CTRL_COLUMNS:     return PE_CTRL_COLUMNS;
      case CTRL_FILESELECT:  return PE_CTRL_FILESELECT;
      case CTRL_FONTSELECT:  return PE_CTRL_FONTSELECT;
    }
    return -1;                  /* CTRL_TABDELAY: no meaning for a host that does its own tab order */
}

static int32_t filter_of(FilereqFilter f)
{
    switch (f) {
      case FILTER_KEY_FILES:     return PE_FILTER_KEY_FILES;
      case FILTER_DYNLIB_FILES:  return PE_FILTER_DYNLIB;
      case FILTER_SOUND_FILES:   return PE_FILTER_SOUND;
      default:                   return PE_FILTER_ALL_FILES;
    }
}

static void describe_ctrl(pe_config *c, int id)
{
    pe_ctrl *k = &c->ctrls[id];
    dlgcontrol *d = k->ctrl;
    int32_t type = type_of(d->type);
    if (type < 0)
        return;

    pe_ctrl_info info;
    memset(&info, 0, sizeof(info));
    info.id = id;
    info.type = type;
    info.group = k->group;
    info.column_start = COLUMN_START(d->column);
    info.column_span = COLUMN_SPAN(d->column);
    info.label = d->label;
    info.align_next_to = -1;
    info.percent_width = 100;
    if (d->align_next_to) {
        for (int i = 0; i < c->nctrls; i++) {
            if (c->ctrls[i].ctrl == d->align_next_to)
                info.align_next_to = i;
        }
    }

    char *shortcuts = NULL;
    const char **labels = NULL;
    switch (d->type) {
      case CTRL_EDITBOX:
        info.shortcut = (unsigned char)d->editbox.shortcut;
        info.percent_width = d->editbox.percentwidth;
        if (d->editbox.password)
            info.flags |= PE_CTRLF_PASSWORD;
        if (d->editbox.has_list)
            info.flags |= PE_CTRLF_HAS_LIST;
        break;
      case CTRL_RADIO: {
        info.shortcut = (unsigned char)d->radio.shortcut;
        info.ncolumns = d->radio.ncolumns;
        info.nbuttons = d->radio.nbuttons;
        labels = snewn(d->radio.nbuttons ? d->radio.nbuttons : 1, const char *);
        shortcuts = snewn(d->radio.nbuttons + 1, char);
        for (int i = 0; i < d->radio.nbuttons; i++) {
            labels[i] = d->radio.buttons[i];
            char sc = d->radio.shortcuts ? d->radio.shortcuts[i] : NO_SHORTCUT;
            shortcuts[i] = sc == NO_SHORTCUT ? ' ' : sc;
        }
        shortcuts[d->radio.nbuttons] = '\0';
        info.buttons = labels;
        info.shortcuts = shortcuts;
        break;
      }
      case CTRL_CHECKBOX:
        info.shortcut = (unsigned char)d->checkbox.shortcut;
        break;
      case CTRL_BUTTON:
        info.shortcut = (unsigned char)d->button.shortcut;
        if (d->button.isdefault)
            info.flags |= PE_CTRLF_DEFAULT;
        if (d->button.iscancel)
            info.flags |= PE_CTRLF_CANCEL;
        break;
      case CTRL_LISTBOX:
        info.shortcut = (unsigned char)d->listbox.shortcut;
        info.list_height = d->listbox.height;
        info.multisel = d->listbox.multisel;
        info.percent_width = d->listbox.percentwidth;
        info.ncolumns = d->listbox.ncols;
        info.percentages = d->listbox.percentages;
        if (d->listbox.draglist)
            info.flags |= PE_CTRLF_DRAGLIST;
        if (d->listbox.hscroll)
            info.flags |= PE_CTRLF_HSCROLL;
        break;
      case CTRL_FILESELECT:
        info.shortcut = (unsigned char)d->fileselect.shortcut;
        info.title = d->fileselect.title;
        info.filter = filter_of(d->fileselect.filter);
        if (d->fileselect.for_writing)
            info.flags |= PE_CTRLF_FOR_WRITING;
        if (d->fileselect.just_button)
            info.flags |= PE_CTRLF_JUST_BUTTON;
        break;
      case CTRL_FONTSELECT:
        info.shortcut = (unsigned char)d->fontselect.shortcut;
        break;
      case CTRL_COLUMNS:
        info.ncolumns = d->columns.ncols;
        info.percentages = d->columns.percentages;
        break;
      case CTRL_TEXT:
        if (d->text.wrap)
            info.flags |= PE_CTRLF_WRAP;
        break;
    }

    HOST_EVENT(c, control, &info);
    sfree(labels);
    sfree(shortcuts);
}

PE_API int32_t pe_config_describe(pe_config *c)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!config_is_live(c) || c->closing)
        return PE_ERR_STATE;

    int id = 0;
    for (size_t s = 0; s < c->box->nctrlsets && !c->closing; s++) {
        struct controlset *set = c->box->ctrlsets[s];
        HOST_EVENT(c, group, (int32_t)s, set->pathname ? set->pathname : "", set->boxname ? set->boxname : "",
                   set->boxtitle ? set->boxtitle : "");
        for (size_t i = 0; i < set->ncontrols && !c->closing; i++, id++)
            describe_ctrl(c, id);
    }
    return PE_OK;
}

PE_API int32_t pe_config_refresh(pe_config *c)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!config_is_live(c) || c->closing)
        return PE_ERR_STATE;
    dlg_refresh(NULL, DLGPARAM(c));
    return PE_OK;
}

/* ----- The functions PuTTY's handlers call ------------------------------------------------- */

void dlg_update_start(dlgcontrol *ctrl, dlgparam *dp)
{
    (void)ctrl;
    HOST_EVENT(CFG(dp), update, 1);
}

void dlg_update_done(dlgcontrol *ctrl, dlgparam *dp)
{
    (void)ctrl;
    HOST_EVENT(CFG(dp), update, 0);
}

void dlg_refresh(dlgcontrol *ctrl, dlgparam *dp)
{
    pe_config *c = CFG(dp);
    if (ctrl) {
        pe_ctrl *k = find_ctrl(dp, ctrl);
        if (k)
            run_handler(c, k, EVENT_REFRESH);
        return;
    }
    HOST_EVENT(c, update, 1);
    /* Handlers may add or remove nothing here (the control set is fixed), so a plain loop is safe. */
    for (int i = 0; i < c->nctrls && !c->closing; i++)
        run_handler(c, &c->ctrls[i], EVENT_REFRESH);
    HOST_EVENT(c, update, 0);
}

void dlg_radiobutton_set(dlgcontrol *ctrl, dlgparam *dp, int whichbutton)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    if (!k)
        return;
    k->radio = whichbutton;
    notify_changed(CFG(dp), k);
}

int dlg_radiobutton_get(dlgcontrol *ctrl, dlgparam *dp)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    return k ? k->radio : -1;
}

void dlg_checkbox_set(dlgcontrol *ctrl, dlgparam *dp, bool checked)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    if (!k)
        return;
    k->checked = checked;
    notify_changed(CFG(dp), k);
}

bool dlg_checkbox_get(dlgcontrol *ctrl, dlgparam *dp)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    return k ? k->checked : false;
}

static void set_text(pe_ctrl *k, const char *utf8)
{
    char *copy = dupstr(utf8 ? utf8 : "");
    sfree(k->text);
    k->text = copy;
}

void dlg_editbox_set(dlgcontrol *ctrl, dlgparam *dp, char const *text)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    if (!k)
        return;
    char *utf8 = pe_text_to_utf8(text, false);
    set_text(k, utf8);
    sfree(utf8);
    notify_changed(CFG(dp), k);
}

void dlg_editbox_set_utf8(dlgcontrol *ctrl, dlgparam *dp, char const *text)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    if (!k)
        return;
    set_text(k, text);
    notify_changed(CFG(dp), k);
}

char *dlg_editbox_get(dlgcontrol *ctrl, dlgparam *dp)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    return pe_text_from_utf8(k ? k->text : "", false);
}

char *dlg_editbox_get_utf8(dlgcontrol *ctrl, dlgparam *dp)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    return dupstr(k ? k->text : "");
}

void dlg_editbox_select_range(dlgcontrol *ctrl, dlgparam *dp, size_t start, size_t len)
{
    (void)ctrl; (void)dp; (void)start; (void)len;      /* a cosmetic: the host selects what it likes */
}

void dlg_text_set(dlgcontrol *ctrl, dlgparam *dp, char const *text)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    if (!k)
        return;
    char *utf8 = pe_text_to_utf8(text, false);
    set_text(k, utf8);
    sfree(utf8);
    notify_changed(CFG(dp), k);
}

void dlg_label_change(dlgcontrol *ctrl, dlgparam *dp, char const *text)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    if (!k)
        return;
    char *utf8 = pe_text_to_utf8(text, false);
    HOST_EVENT(CFG(dp), label_changed, (int32_t)ctrl_id(CFG(dp), k), utf8);
    sfree(utf8);
}

/* ----- List boxes (and the list part of combo boxes) ----- */

void dlg_listbox_clear(dlgcontrol *ctrl, dlgparam *dp)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    if (!k)
        return;
    list_free(k);
    notify_changed(CFG(dp), k);
}

void dlg_listbox_del(dlgcontrol *ctrl, dlgparam *dp, int index)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    if (!k || index < 0 || index >= k->nitems)
        return;
    sfree(k->items[index].text);
    memmove(&k->items[index], &k->items[index + 1], sizeof(pe_list_item) * (k->nitems - index - 1));
    k->nitems--;
    notify_changed(CFG(dp), k);
}

void dlg_listbox_addwithid(dlgcontrol *ctrl, dlgparam *dp, char const *text, int id)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    if (!k)
        return;
    sgrowarray(k->items, k->itemsize, k->nitems);
    pe_list_item *item = &k->items[k->nitems++];
    item->text = pe_text_to_utf8(text, false);
    item->id = id;
    item->selected = false;
    notify_changed(CFG(dp), k);
}

void dlg_listbox_add(dlgcontrol *ctrl, dlgparam *dp, char const *text)
{
    dlg_listbox_addwithid(ctrl, dp, text, 0);
}

int dlg_listbox_getid(dlgcontrol *ctrl, dlgparam *dp, int index)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    if (!k || index < 0 || index >= k->nitems)
        return -1;
    return k->items[index].id;
}

int dlg_listbox_index(dlgcontrol *ctrl, dlgparam *dp)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    if (!k)
        return -1;
    int found = -1;
    for (int i = 0; i < k->nitems; i++) {
        if (k->items[i].selected) {
            if (found >= 0)
                return -1;      /* more than one */
            found = i;
        }
    }
    return found;
}

bool dlg_listbox_issel(dlgcontrol *ctrl, dlgparam *dp, int index)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    return k && index >= 0 && index < k->nitems && k->items[index].selected;
}

void dlg_listbox_select(dlgcontrol *ctrl, dlgparam *dp, int index)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    if (!k)
        return;
    for (int i = 0; i < k->nitems; i++)
        k->items[i].selected = (i == index);
    notify_changed(CFG(dp), k);
}

/* ----- Files, fonts, colours ----- */

void dlg_filesel_set(dlgcontrol *ctrl, dlgparam *dp, Filename *fn)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    if (!k)
        return;
    Filename *copy = fn ? filename_copy(fn) : NULL;
    if (k->file)
        filename_free(k->file);
    k->file = copy;
    notify_changed(CFG(dp), k);
}

Filename *dlg_filesel_get(dlgcontrol *ctrl, dlgparam *dp)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    return (k && k->file) ? filename_copy(k->file) : filename_from_str("");
}

void dlg_fontsel_set(dlgcontrol *ctrl, dlgparam *dp, FontSpec *fn)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    if (!k)
        return;
    FontSpec *copy = fn ? fontspec_copy(fn) : NULL;
    if (k->font)
        fontspec_free(k->font);
    k->font = copy;
    notify_changed(CFG(dp), k);
}

FontSpec *dlg_fontsel_get(dlgcontrol *ctrl, dlgparam *dp)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    return (k && k->font) ? fontspec_copy(k->font) : fontspec_new_default();
}

void dlg_coloursel_start(dlgcontrol *ctrl, dlgparam *dp, int r, int g, int b)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    if (!k)
        return;
    k->colour_ok = false;
    HOST_EVENT(CFG(dp), colour_request, (int32_t)ctrl_id(CFG(dp), k), r, g, b);
}

bool dlg_coloursel_results(dlgcontrol *ctrl, dlgparam *dp, int *r, int *g, int *b)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    if (!k || !k->colour_ok)
        return false;
    *r = k->colour_r;
    *g = k->colour_g;
    *b = k->colour_b;
    return true;
}

/* ----- Focus, visibility, messages, the end ----- */

void dlg_set_focus(dlgcontrol *ctrl, dlgparam *dp)
{
    pe_ctrl *k = find_ctrl(dp, ctrl);
    if (k)
        HOST_EVENT(CFG(dp), focus, (int32_t)ctrl_id(CFG(dp), k));
}

dlgcontrol *dlg_last_focused(dlgcontrol *ctrl, dlgparam *dp)
{
    pe_config *c = CFG(dp);
    pe_ctrl *self = find_ctrl(dp, ctrl);
    int self_id = self ? ctrl_id(c, self) : -1;
    int pick = c->focus[0] != self_id ? c->focus[0] : c->focus[1];
    pe_ctrl *k = ctrl_by_id(c, pick);
    return k ? k->ctrl : NULL;
}

bool dlg_is_visible(dlgcontrol *ctrl, dlgparam *dp)
{
    pe_config *c = CFG(dp);
    pe_ctrl *k = find_ctrl(dp, ctrl);
    if (!k || !c->page || !*c->page)
        return true;            /* the host did not say which page it shows: everything counts as visible */
    const char *path = c->box->ctrlsets[k->group]->pathname;
    return path && strcmp(path, c->page) == 0;
}

void dlg_beep(dlgparam *dp)
{
    HOST_EVENT_NOARGS(CFG(dp), beep);
}

void dlg_error_msg(dlgparam *dp, const char *msg)
{
    char *utf8 = pe_text_to_utf8(msg, false);
    HOST_EVENT(CFG(dp), message, utf8, 1);
    sfree(utf8);
}

void dlg_end(dlgparam *dp, int value)
{
    HOST_EVENT(CFG(dp), end, value);
}

/* The host-CA list is a dialog of its own (windows/dialog.c); ours comes with a later page of the settings. */
void show_ca_config_box(dlgparam *dp)
{
    HOST_EVENT(CFG(dp), message, "Editing the trusted host certification authorities is not available in this version.", 0);
}

/* ----- What the user did -------------------------------------------------------------------- */

#define USER_CALL_PROLOGUE(c, k, id)                                                 \
    int32_t rc__ = pe_check_engine_thread();                                         \
    if (rc__ != PE_OK)                                                               \
        return rc__;                                                                 \
    if (!config_is_live(c) || (c)->closing)                                          \
        return PE_ERR_STATE;                                                         \
    pe_ctrl *k = ctrl_by_id((c), (id));                                              \
    if (!k)                                                                          \
        return PE_ERR_FAILED

PE_API int32_t pe_config_set_page(pe_config *c, const char *path)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!config_is_live(c))
        return PE_ERR_STATE;
    sfree(c->page);
    c->page = (path && *path) ? dupstr(path) : NULL;
    return PE_OK;
}

PE_API int32_t pe_config_user_focus(pe_config *c, int32_t id)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!config_is_live(c) || c->closing)
        return PE_ERR_STATE;
    if (!ctrl_by_id(c, id))
        return PE_ERR_FAILED;
    if (c->focus[0] != id) {
        c->focus[1] = c->focus[0];
        c->focus[0] = id;
    }
    return PE_OK;
}

PE_API int32_t pe_config_user_set_text(pe_config *c, int32_t id, const char *utf8)
{
    USER_CALL_PROLOGUE(c, k, id);
    if (k->ctrl->type != CTRL_EDITBOX || !utf8)
        return PE_ERR_FAILED;
    set_text(k, utf8);
    run_handler(c, k, EVENT_VALCHANGE);
    return PE_OK;
}

PE_API int32_t pe_config_user_set_checked(pe_config *c, int32_t id, int32_t checked)
{
    USER_CALL_PROLOGUE(c, k, id);
    if (k->ctrl->type != CTRL_CHECKBOX)
        return PE_ERR_FAILED;
    k->checked = checked != 0;
    run_handler(c, k, EVENT_VALCHANGE);
    return PE_OK;
}

PE_API int32_t pe_config_user_set_radio(pe_config *c, int32_t id, int32_t button)
{
    USER_CALL_PROLOGUE(c, k, id);
    if (k->ctrl->type != CTRL_RADIO || button < 0 || button >= k->ctrl->radio.nbuttons)
        return PE_ERR_FAILED;
    k->radio = button;
    run_handler(c, k, EVENT_VALCHANGE);
    return PE_OK;
}

PE_API int32_t pe_config_user_select(pe_config *c, int32_t id, int32_t index, int32_t selected)
{
    USER_CALL_PROLOGUE(c, k, id);
    if (k->ctrl->type != CTRL_LISTBOX || index < 0 || index >= k->nitems)
        return PE_ERR_FAILED;
    if (k->ctrl->listbox.multisel) {
        k->items[index].selected = selected != 0;
    } else if (selected) {
        for (int i = 0; i < k->nitems; i++)
            k->items[i].selected = (i == index);
    } else {
        k->items[index].selected = false;
    }
    run_handler(c, k, EVENT_SELCHANGE);
    return PE_OK;
}

PE_API int32_t pe_config_user_action(pe_config *c, int32_t id)
{
    USER_CALL_PROLOGUE(c, k, id);
    run_handler(c, k, EVENT_ACTION);
    return PE_OK;
}

PE_API int32_t pe_config_user_move(pe_config *c, int32_t id, int32_t from, int32_t to)
{
    USER_CALL_PROLOGUE(c, k, id);
    if (k->ctrl->type != CTRL_LISTBOX || !k->ctrl->listbox.draglist || from < 0 || from >= k->nitems ||
        to < 0 || to >= k->nitems)
        return PE_ERR_FAILED;
    if (from != to) {
        pe_list_item moved = k->items[from];
        if (from < to)
            memmove(&k->items[from], &k->items[from + 1], sizeof(pe_list_item) * (to - from));
        else
            memmove(&k->items[to + 1], &k->items[to], sizeof(pe_list_item) * (from - to));
        k->items[to] = moved;
    }
    /* PuTTY's handlers read the whole order back on a value change (the cipher preference list does). */
    run_handler(c, k, EVENT_VALCHANGE);
    return PE_OK;
}

PE_API int32_t pe_config_user_set_file(pe_config *c, int32_t id, const char *utf8_path)
{
    USER_CALL_PROLOGUE(c, k, id);
    if (k->ctrl->type != CTRL_FILESELECT || !utf8_path)
        return PE_ERR_FAILED;
    Filename *fn = filename_from_str(utf8_path);
    if (k->file)
        filename_free(k->file);
    k->file = fn;
    /* A "just a button" file selector reports a chosen name as an action; the others as a changed value. */
    run_handler(c, k, k->ctrl->fileselect.just_button ? EVENT_ACTION : EVENT_VALCHANGE);
    return PE_OK;
}

PE_API int32_t pe_config_user_set_font(pe_config *c, int32_t id, const char *name, int32_t height,
                                       int32_t bold, int32_t charset)
{
    USER_CALL_PROLOGUE(c, k, id);
    if (k->ctrl->type != CTRL_FONTSELECT || !name)
        return PE_ERR_FAILED;
    FontSpec *font = fontspec_new(name, bold != 0, height, charset);
    if (k->font)
        fontspec_free(k->font);
    k->font = font;
    run_handler(c, k, EVENT_VALCHANGE);
    return PE_OK;
}

PE_API int32_t pe_config_colour_result(pe_config *c, int32_t id, int32_t ok, int32_t r, int32_t g, int32_t b)
{
    USER_CALL_PROLOGUE(c, k, id);
    k->colour_ok = ok != 0;
    k->colour_r = r;
    k->colour_g = g;
    k->colour_b = b;
    run_handler(c, k, EVENT_CALLBACK);
    return PE_OK;
}

/* ----- Reading state ------------------------------------------------------------------------- */

static int32_t copy_out(const char *text, char *buffer, int32_t capacity)
{
    size_t length = strlen(text);
    if (buffer && capacity > 0) {
        size_t n = length < (size_t)capacity - 1 ? length : (size_t)capacity - 1;
        memcpy(buffer, text, n);
        buffer[n] = '\0';
    }
    return (int32_t)length;
}

#define GETTER_PROLOGUE(c, k, id)                                                    \
    int32_t rc__ = pe_check_engine_thread();                                         \
    if (rc__ != PE_OK)                                                               \
        return rc__;                                                                 \
    if (!config_is_live(c))                                                          \
        return PE_ERR_STATE;                                                         \
    pe_ctrl *k = ctrl_by_id((c), (id));                                              \
    if (!k)                                                                          \
        return PE_ERR_FAILED

PE_API int32_t pe_config_get_text(pe_config *c, int32_t id, char *buffer, int32_t capacity)
{
    GETTER_PROLOGUE(c, k, id);
    return copy_out(k->text, buffer, capacity);
}

PE_API int32_t pe_config_get_checked(pe_config *c, int32_t id)
{
    GETTER_PROLOGUE(c, k, id);
    return k->checked ? 1 : 0;
}

PE_API int32_t pe_config_get_radio(pe_config *c, int32_t id)
{
    GETTER_PROLOGUE(c, k, id);
    return k->radio;
}

PE_API int32_t pe_config_list_count(pe_config *c, int32_t id)
{
    GETTER_PROLOGUE(c, k, id);
    return k->nitems;
}

PE_API int32_t pe_config_list_item(pe_config *c, int32_t id, int32_t index, char *buffer, int32_t capacity,
                                   int32_t *item_id, int32_t *selected)
{
    GETTER_PROLOGUE(c, k, id);
    if (index < 0 || index >= k->nitems)
        return PE_ERR_FAILED;
    if (item_id)
        *item_id = k->items[index].id;
    if (selected)
        *selected = k->items[index].selected ? 1 : 0;
    return copy_out(k->items[index].text, buffer, capacity);
}

PE_API int32_t pe_config_get_file(pe_config *c, int32_t id, char *buffer, int32_t capacity)
{
    GETTER_PROLOGUE(c, k, id);
    return copy_out(k->file ? filename_to_str(k->file) : "", buffer, capacity);
}

PE_API int32_t pe_config_get_font(pe_config *c, int32_t id, char *name, int32_t capacity, int32_t *height,
                                  int32_t *bold, int32_t *charset)
{
    GETTER_PROLOGUE(c, k, id);
    if (!k->font)
        return PE_ERR_STATE;
    if (height)
        *height = k->font->height;
    if (bold)
        *bold = k->font->isbold ? 1 : 0;
    if (charset)
        *charset = k->font->charset;
    return copy_out(k->font->name ? k->font->name : "", name, capacity);
}

/* ----- Starting a session, closing ------------------------------------------------------------ */

PE_API int32_t pe_config_start_session(pe_config *c, const pe_session_events *events, void *user,
                                       pe_session **out_session)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!config_is_live(c) || c->closing || c->midsession)
        return PE_ERR_STATE;                /* a box on a running session changes that session; it does not start one */
    return pe_session_open_conf(c->conf, events, user, out_session);
}

PE_API int32_t pe_config_close(pe_config *c)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!config_is_live(c))
        return PE_ERR_STATE;
    if (c->busy > 0) {
        /* PuTTY's handler (or a host callback) is still running: the dialog goes once it has returned. */
        if (!c->closing) {
            c->closing = true;
            queue_toplevel_callback(config_destroy_callback, c);
        }
        return PE_OK;
    }
    config_destroy(c);
    return PE_OK;
}
