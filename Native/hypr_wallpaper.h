#ifndef HYPR_WALLPAPER_H
#define HYPR_WALLPAPER_H

#ifdef __cplusplus
extern "C"
{
#endif

    typedef struct hypr_wallpaper hypr_wallpaper;

    /* All calls, including destruction, belong to one caller-owned worker thread.
     * create owns an independent Wayland connection and returns NULL on failure.
     * get_error(NULL) reports the most recent create failure on the calling thread.
     * Startup synchronization has a two-second deadline after connecting; the
     * local wl_display_connect itself is subject to the platform's connect behavior.
     */
    hypr_wallpaper *hypr_wallpaper_create(void);
    void hypr_wallpaper_destroy(hypr_wallpaper *handle);

    /* Nonblocking event processing and deferred drawing. Call regularly even when
     * the image is unchanged. Returns 0 on a terminal connection/rendering failure.
     */
    int hypr_wallpaper_poll(hypr_wallpaper *handle);

    /* Copies RGBA bytes before returning; stride is in bytes and must be >= width*4.
     * RGB is rendered opaque (alpha is ignored), center-cropped with cover scaling.
     * The first image is immediate; replacements crossfade over two monotonic
     * seconds, paced at approximately 30 fps when poll is called frequently enough.
     * Interruptions start from each output's last submitted blend.
     * Success means accepted, not necessarily presented. No compositor waits occur.
     * Invalid input leaves the previous image intact and is not a terminal error.
     */
    int hypr_wallpaper_set_image(hypr_wallpaper *handle, const unsigned char *rgba, int width, int height, int stride);

    /* Borrowed diagnostic string, valid until the next call on this thread/handle.
     * No logging is performed by this library. NULL handles are accepted here and
     * by destroy; poll/set_image return 0 for NULL.
     */
    const char *hypr_wallpaper_get_error(hypr_wallpaper *handle);

#ifdef __cplusplus
}
#endif

#endif
