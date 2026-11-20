// App icons from the owner's art, used as is: every icon is a crop of one of the owner's renders, resized.
// Nothing is drawn, padded, tiled, badged or recoloured. The OS applies its own mask (iOS squircle, Android
// adaptive shape, tvOS rounded rect), so each crop is chosen to survive it.
//
//   swift scripts/make-app-icons.swift crop <icon-candidates dir>   # refresh art/app-icons/*.png from the originals
//   swift scripts/make-app-icons.swift apple|android|desktop
//
// The same file lives in AlUmmahNowApple, AlUmmahNowAndroid and AlUmmahNowDesktop; each repo runs its own mode.
// Art (owner's choice, matching the web apps in AlUmmahNow packages/app-icons):
//   Viewer  setC-videos-magnific-v1, framing D (the lit windows with the play button), as the videos web app
//   Admin   setC-staff-ring-octagon, the whole square (the octagon frame is the admins' mark), as videos admin
//   Studio  setC-videos-magnific-v1, the whole square (the whole lantern; owner's pick, 2026-11-20)
import Accelerate
import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

// MARK: - The art

/// Frame-free square of each original (1376 x 768), measured by `cropBox` in AlUmmahNow packages/app-icons.
let masters: [String: (file: String, left: Int, top: Int, size: Int)] = [
    "videos": ("setC-videos-magnific-v1.png", 411, 107, 554),
    "admin": ("setC-staff-ring-octagon.png", 411, 107, 554),
]

/// A crop inside a master, as fractions of its side (left, top, width, height).
struct Frame { let master: String; let x, y, w, h: Double }

/// Framings on the 512 grid of packages/app-icons FRAMING.
func square(_ master: String, _ l: Double, _ t: Double, _ s: Double) -> Frame {
    Frame(master: master, x: l / 512, y: t / 512, w: s / 512, h: s / 512)
}


/// A wide crop of the videos art for TV (full width, `aspect` = width / height), centred on framing D's centre.
func wide(_ aspect: Double) -> Frame {
    let h = 1 / aspect, centre = (180.0 + 150) / 512
    return Frame(master: "videos", x: 0, y: min(max(centre - h / 2, 0), 1 - h), w: 1, h: h)
}

// MARK: - Arguments

let args = Array(CommandLine.arguments.dropFirst())
let apps: [String: Frame] = [
    "viewer": square("videos", 115, 180, 300),
    "admin": square("admin", 0, 0, 512),
    "studio": square("videos", 0, 0, 512),
]
let artDir = "art/app-icons"

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data((message + "\n").utf8))
    exit(2)
}

// MARK: - Images

func load(_ path: String) -> CGImage {
    guard let source = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil),
        let image = CGImageSourceCreateImageAtIndex(source, 0, nil)
    else { fail("cannot read \(path)") }
    return image
}

func write(_ image: CGImage, _ path: String) {
    let url = URL(fileURLWithPath: path)
    try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
    guard let destination = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil)
    else { fail("cannot write \(path)") }
    CGImageDestinationAddImage(destination, image, nil)
    CGImageDestinationFinalize(destination)
}

func json(_ object: Any, _ path: String) {
    let url = URL(fileURLWithPath: path)
    try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
    let data = try! JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys])
    try! data.write(to: url)
}

func text(_ string: String, _ path: String) {
    let url = URL(fileURLWithPath: path)
    try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
    try! string.write(to: url, atomically: true, encoding: .utf8)
}

let sRGB = CGColorSpace(name: CGColorSpace.sRGB)!

/// Opaque RGB copy (the art has no transparency; App Store icons must not have an alpha channel).
func opaque(_ image: CGImage) -> CGImage {
    let context = CGContext(
        data: nil, width: image.width, height: image.height, bitsPerComponent: 8, bytesPerRow: 0, space: sRGB,
        bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)!
    context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
    return context.makeImage()!
}

/// Lanczos resize (vImage high-quality resampling), opaque.
func resize(_ image: CGImage, _ width: Int, _ height: Int) -> CGImage {
    let format = vImage_CGImageFormat(
        bitsPerComponent: 8, bitsPerPixel: 32, colorSpace: Unmanaged.passUnretained(sRGB),
        bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipLast.rawValue), version: 0, decode: nil,
        renderingIntent: .defaultIntent)
    var source = try! vImage_Buffer(cgImage: image, format: format)
    defer { source.free() }
    var target = try! vImage_Buffer(width: width, height: height, bitsPerPixel: 32)
    defer { target.free() }
    vImageScale_ARGB8888(&source, &target, nil, vImage_Flags(kvImageHighQualityResampling))
    return try! target.createCGImage(format: format)
}

var cache: [String: CGImage] = [:]
func master(_ name: String) -> CGImage {
    if let image = cache[name] { return image }
    let image = load("\(artDir)/\(name).png")
    cache[name] = image
    return image
}

/// The crop of `frame`, resized to width x height. The crop's aspect must match.
func icon(_ frame: Frame, _ width: Int, _ height: Int) -> CGImage {
    let art = master(frame.master)
    let side = Double(art.width)
    let rect = CGRect(x: frame.x * side, y: frame.y * side, width: frame.w * side, height: frame.h * side).integral
    return resize(art.cropping(to: rect)!, width, height)
}

// MARK: - crop: masters from the originals

func cropMasters(_ candidates: String) {
    for (name, m) in masters {
        let original = load("\(candidates)/\(m.file)")
        let square = original.cropping(to: CGRect(x: m.left, y: m.top, width: m.size, height: m.size))!
        write(opaque(square), "\(artDir)/\(name).png")
    }
}

// MARK: - Apple

let xcode = ["author": "xcode", "version": 1] as [String: Any]

func appleIOS(_ target: String, _ frame: Frame) {
    let set = "Apps/\(target)/Assets.xcassets/AppIcon.appiconset"
    write(icon(frame, 1024, 1024), "\(set)/AppIcon-1024.png")
    json(
        [
            "images": [["filename": "AppIcon-1024.png", "idiom": "universal", "platform": "ios", "size": "1024x1024"]],
            "info": xcode,
        ], "\(set)/Contents.json")
    json(["info": xcode], "Apps/\(target)/Assets.xcassets/Contents.json")
}

func appleMac(_ target: String, _ frame: Frame) {
    let set = "Apps/\(target)/Assets.xcassets/AppIcon.appiconset"
    var images: [[String: String]] = []
    for size in [16, 32, 128, 256, 512] {
        for scale in [1, 2] {
            let name = "AppIcon-\(size)@\(scale)x.png"
            write(icon(frame, size * scale, size * scale), "\(set)/\(name)")
            images.append(["filename": name, "idiom": "mac", "scale": "\(scale)x", "size": "\(size)x\(size)"])
        }
    }
    json(["images": images, "info": xcode], "\(set)/Contents.json")
    json(["info": xcode], "Apps/\(target)/Assets.xcassets/Contents.json")
}

/// tvOS layered icon: the art is the back layer; the front layer is empty (the art is one flat render).
func appleImageStack(_ path: String, _ width: Int, _ height: Int, scales: [Int]) {
    json(
        ["info": xcode, "layers": [["filename": "Front.imagestacklayer"], ["filename": "Back.imagestacklayer"]]],
        "\(path)/Contents.json")
    for layer in ["Back", "Front"] {
        let dir = "\(path)/\(layer).imagestacklayer"
        json(["info": xcode], "\(dir)/Contents.json")
        var images: [[String: String]] = []
        for scale in scales {
            let name = "\(layer)@\(scale)x.png"
            let (w, h) = (width * scale, height * scale)
            let image: CGImage
            if layer == "Back" {
                image = icon(wide(Double(width) / Double(height)), w, h)
            } else {
                let context = CGContext(
                    data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0, space: sRGB,
                    bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
                image = context.makeImage()!
            }
            write(image, "\(dir)/Content.imageset/\(name)")
            images.append(["filename": name, "idiom": "tv", "scale": "\(scale)x"])
        }
        json(["images": images, "info": xcode], "\(dir)/Content.imageset/Contents.json")
    }
}

func appleTopShelf(_ path: String, _ width: Int, _ height: Int) {
    var images: [[String: String]] = []
    for scale in [1, 2] {
        let name = "TopShelf@\(scale)x.png"
        write(icon(wide(Double(width) / Double(height)), width * scale, height * scale), "\(path)/\(name)")
        images.append(["filename": name, "idiom": "tv", "scale": "\(scale)x"])
    }
    json(["images": images, "info": xcode], "\(path)/Contents.json")
}

func appleTV(_ target: String) {
    let brand = "Apps/\(target)/Assets.xcassets/App Icon & Top Shelf Image.brandassets"
    appleImageStack("\(brand)/App Icon.imagestack", 400, 240, scales: [1, 2])
    appleImageStack("\(brand)/App Icon - App Store.imagestack", 1280, 768, scales: [1])
    appleTopShelf("\(brand)/Top Shelf Image.imageset", 1920, 720)
    appleTopShelf("\(brand)/Top Shelf Image Wide.imageset", 2320, 720)
    json(
        [
            "assets": [
                ["filename": "App Icon - App Store.imagestack", "idiom": "tv", "role": "primary-app-icon", "size": "1280x768"],
                ["filename": "App Icon.imagestack", "idiom": "tv", "role": "primary-app-icon", "size": "400x240"],
                ["filename": "Top Shelf Image Wide.imageset", "idiom": "tv", "role": "top-shelf-image-wide", "size": "2320x720"],
                ["filename": "Top Shelf Image.imageset", "idiom": "tv", "role": "top-shelf-image", "size": "1920x720"],
            ], "info": xcode,
        ], "\(brand)/Contents.json")
    json(["info": xcode], "Apps/\(target)/Assets.xcassets/Contents.json")
}

// MARK: - Android

/// Adaptive icon: the art fills the 72 dp viewport the launcher shows (inset 18 of 108 dp) and the launcher masks it.
/// The 18 dp band outside the viewport is never shown, so it stays empty instead of being filled with anything.
let densities: [(String, Double)] = [("mdpi", 1), ("hdpi", 1.5), ("xhdpi", 2), ("xxhdpi", 3), ("xxxhdpi", 4)]

func androidLauncher(_ module: String, _ frame: Frame) {
    let res = "\(module)/src/main/res"
    for (density, scale) in densities {
        let side = Int(72 * scale)
        write(icon(frame, side, side), "\(res)/drawable-\(density)/aun_app_icon_art.png")
    }
    text("""
    <?xml version="1.0" encoding="utf-8"?>
    <!-- Generated by scripts/make-app-icons.swift: the owner's art fills the 72 dp viewport the launcher masks. -->
    <inset xmlns:android="http://schemas.android.com/apk/res/android"
        android:drawable="@drawable/aun_app_icon_art"
        android:inset="16.6667%" />

    """, "\(res)/drawable/aun_app_icon_layer.xml")
    // minSdk 26: every launcher reads the adaptive icon, so there are no legacy PNG mipmaps.
    text("""
    <?xml version="1.0" encoding="utf-8"?>
    <!-- Generated by scripts/make-app-icons.swift. No monochrome layer: the art is never recoloured. -->
    <adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
        <background android:drawable="@android:color/transparent" />
        <foreground android:drawable="@drawable/aun_app_icon_layer" />
    </adaptive-icon>

    """, "\(res)/mipmap-anydpi/ic_launcher.xml")
}

/// Android TV banner, 320 x 180 dp: a 16:9 crop of the videos art.
func androidBanner(_ module: String) {
    for (density, scale) in densities {
        write(
            icon(wide(16.0 / 9), Int(320 * scale), Int(180 * scale)),
            "\(module)/src/main/res/drawable-\(density)/tv_banner.png")
    }
}

// MARK: - Desktop

/// Windows .ico with PNG frames (16-256), each its own resize.
func ico(_ frame: Frame, _ path: String) {
    let sizes = [16, 20, 24, 32, 40, 48, 64, 256]
    var frames: [Data] = []
    for size in sizes {
        let data = NSMutableData()
        let destination = CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil)!
        CGImageDestinationAddImage(destination, icon(frame, size, size), nil)
        CGImageDestinationFinalize(destination)
        frames.append(data as Data)
    }
    var out = Data([0, 0, 1, 0, UInt8(sizes.count), 0])
    var offset = 6 + 16 * sizes.count
    func le16(_ v: Int) -> [UInt8] { [UInt8(v & 0xff), UInt8(v >> 8 & 0xff)] }
    func le32(_ v: Int) -> [UInt8] { le16(v & 0xffff) + le16(v >> 16) }
    for (size, png) in zip(sizes, frames) {
        let edge = UInt8(size >= 256 ? 0 : size)
        out.append(contentsOf: [edge, edge, 0, 0] + le16(1) + le16(32) + le32(png.count) + le32(offset))
        offset += png.count
    }
    for png in frames { out.append(png) }
    try! out.write(to: URL(fileURLWithPath: path))
}

/// MSIX assets: square logos at every scale and the unplated target sizes the taskbar and Start use.
func windows(_ project: String, _ frame: Frame) {
    let assets = "src/\(project)/Assets"
    for (name, base) in [("Square44x44Logo", 44), ("Square150x150Logo", 150), ("StoreLogo", 50)] {
        for scale in [100, 125, 150, 200, 400] {
            let side = Int((Double(base) * Double(scale) / 100).rounded())
            write(icon(frame, side, side), "\(assets)/\(name).scale-\(scale).png")
        }
    }
    for size in [16, 24, 32, 48, 256] {
        write(icon(frame, size, size), "\(assets)/Square44x44Logo.targetsize-\(size).png")
        write(icon(frame, size, size), "\(assets)/Square44x44Logo.targetsize-\(size)_altform-unplated.png")
        write(icon(frame, size, size), "\(assets)/Square44x44Logo.targetsize-\(size)_altform-lightunplated.png")
    }
    ico(frame, "\(assets)/app.ico")
}

/// Linux hicolor icons, one PNG per size, named after the app id.
func linux(_ appID: String, _ frame: Frame) {
    for size in [16, 24, 32, 48, 64, 128, 256, 512] {
        write(icon(frame, size, size), "packaging/icons/hicolor/\(size)x\(size)/apps/\(appID).png")
    }
}

// MARK: - Run

switch args.first {
case "crop":
    guard args.count == 2 else { fail("usage: swift scripts/make-app-icons.swift crop <icon-candidates dir>") }
    cropMasters(args[1])
case "apple":
    for (target, app) in [
        ("ViewerIOS", "viewer"), ("StudioIOS", "studio"), ("AdminIOS", "admin"),
    ] { appleIOS(target, apps[app]!) }
    for (target, app) in [
        ("ViewerMac", "viewer"), ("StudioMac", "studio"), ("AdminMac", "admin"),
    ] { appleMac(target, apps[app]!) }
    appleTV("ViewerTV")
case "android":
    for (module, app) in [
        ("viewer-mobile", "viewer"), ("viewer-tv", "viewer"), ("studio-mobile", "studio"), ("admin-mobile", "admin"),
    ] { androidLauncher(module, apps[app]!) }
    androidBanner("viewer-tv")
case "desktop":
    for (project, app) in [
        ("AlUmmahNow.Windows", "viewer"), ("AlUmmahNow.Studio.Windows", "studio"), ("AlUmmahNow.Admin.Windows", "admin"),
    ] { windows(project, apps[app]!) }
    linux("net.alummahnow.AlUmmahNow", apps["viewer"]!)
    linux("net.alummahnow.Studio", apps["studio"]!)
    // Studio's header bar shows its own app icon (embedded resource studio/app-icon.png).
    write(icon(apps["studio"]!, 180, 180), "src/AlUmmahNow.Studio/Assets/app-icon.png")
default:
    fail("usage: swift scripts/make-app-icons.swift crop <dir> | apple|android|desktop")
}
