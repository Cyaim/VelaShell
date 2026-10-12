/*
 * xprobe —— 量 VelaShell.XServer 帧率与跟手程度用的 X 客户端(Xlib)。由 perf.cs 在容器里起,窗口标题是 "xprobe-<模式>"。
 *
 *   xprobe follow       指针跟随:每次 MotionNotify(先把排着的合并成最后一个)擦掉旧方块、在指针处画 32×32 的方块再 XFlush
 *                       —— 画图程序的笔刷、拖动的滑块就是这样。量「指针动 → 画面跟上」
 *   xprobe key          每次 KeyPress 整窗换一种颜色再 XFlush。量「按键 → 整窗重画」
 *   xprobe anim W H     整窗动画:每帧 XPutImage 一整幅 W×H、再 XSync 等服务端做完(软件渲染的程序就是这样送帧),
 *                       每秒往 stdout 打一行 "fps <帧率>"
 *
 * 映射并收到第一个 Expose 后往 stdout 打 "ready"。
 */
#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define SQUARE 32

static double now(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return ts.tv_sec + ts.tv_nsec / 1e9;
}

static void wait_exposed(Display *d, Window win)
{
    XEvent ev;
    do
    {
        XWindowEvent(d, win, ExposureMask, &ev);
    } while (ev.xexpose.count != 0);
    printf("ready\n");
    fflush(stdout);
}

static int follow(Display *d, Window win, GC gc, unsigned long fg, unsigned long bg)
{
    int lastX = -1000, lastY = -1000;
    for (;;)
    {
        XEvent ev;
        XNextEvent(d, &ev);
        if (ev.type != MotionNotify)
        {
            continue;
        }
        while (XCheckTypedWindowEvent(d, win, MotionNotify, &ev))
        {
            // 只画最后一个位置(工具包都这么合并)
        }
        XSetForeground(d, gc, bg);
        XFillRectangle(d, win, gc, lastX - SQUARE / 2, lastY - SQUARE / 2, SQUARE, SQUARE);
        lastX = ev.xmotion.x;
        lastY = ev.xmotion.y;
        XSetForeground(d, gc, fg);
        XFillRectangle(d, win, gc, lastX - SQUARE / 2, lastY - SQUARE / 2, SQUARE, SQUARE);
        XFlush(d);
    }
}

static int key(Display *d, Window win, GC gc, int w, int h)
{
    static const unsigned long colors[] = { 0xcc3333, 0x33cc33, 0x3333cc, 0xcccc33 };
    int n = 0;
    for (;;)
    {
        XEvent ev;
        XNextEvent(d, &ev);
        if (ev.type != KeyPress)
        {
            continue;
        }
        XSetForeground(d, gc, colors[n++ % 4]);
        XFillRectangle(d, win, gc, 0, 0, w, h);
        XFlush(d);
    }
}

static int anim(Display *d, Window win, GC gc, int w, int h)
{
    Visual *visual = DefaultVisual(d, DefaultScreen(d));
    uint32_t *pixels = malloc((size_t)w * h * 4);
    XImage *image = XCreateImage(d, visual, 24, ZPixmap, 0, (char *)pixels, w, h, 32, 0);
    if (image == NULL || pixels == NULL)
    {
        fprintf(stderr, "cannot create image\n");
        return 1;
    }
    double second = now();
    int frames = 0;
    for (unsigned frame = 0;; frame++)
    {
        // 一条竖带横着走,底色随帧变:每帧整幅都不一样,便宜(写一遍内存)
        uint32_t base = ((frame * 3) & 0xff) << 8;
        int bar = (int)(frame * 8 % (unsigned)w);
        for (int y = 0; y < h; y++)
        {
            uint32_t *row = pixels + (size_t)y * w;
            for (int x = 0; x < w; x++)
            {
                row[x] = (x >= bar && x < bar + 40) ? 0xffffff : base | (uint32_t)(y & 0xff);
            }
        }
        XPutImage(d, win, gc, image, 0, 0, 0, 0, w, h);
        XSync(d, False);
        while (XPending(d))
        {
            XEvent ev;
            XNextEvent(d, &ev);
        }
        frames++;
        double t = now();
        if (t - second >= 1.0)
        {
            printf("fps %.1f\n", frames / (t - second));
            fflush(stdout);
            frames = 0;
            second = t;
        }
    }
}

int main(int argc, char **argv)
{
    const char *mode = argc > 1 ? argv[1] : "follow";
    int w = 640, h = 480;
    if (strcmp(mode, "anim") == 0 && argc > 3)
    {
        w = atoi(argv[2]);
        h = atoi(argv[3]);
    }
    Display *d = XOpenDisplay(NULL);
    if (d == NULL)
    {
        fprintf(stderr, "cannot open display\n");
        return 1;
    }
    int screen = DefaultScreen(d);
    unsigned long fg = BlackPixel(d, screen), bg = WhitePixel(d, screen);
    Window win = XCreateSimpleWindow(d, RootWindow(d, screen), 0, 0, w, h, 0, fg, bg);
    char title[64];
    snprintf(title, sizeof title, "xprobe-%s", mode);
    XStoreName(d, win, title);
    XSelectInput(d, win, ExposureMask | PointerMotionMask | KeyPressMask | StructureNotifyMask);
    XMapWindow(d, win);
    GC gc = XCreateGC(d, win, 0, NULL);
    wait_exposed(d, win);
    if (strcmp(mode, "follow") == 0)
    {
        return follow(d, win, gc, fg, bg);
    }
    if (strcmp(mode, "key") == 0)
    {
        return key(d, win, gc, w, h);
    }
    if (strcmp(mode, "anim") == 0)
    {
        return anim(d, win, gc, w, h);
    }
    fprintf(stderr, "unknown mode %s\n", mode);
    return 2;
}
